using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalServerManager.Management;

public sealed record ComicCompanionSettings(
    Uri? ApiUrl = null,
    Uri? BrowserUrl = null,
    string? InstallationFolder = null,
    bool StartWithApp = false,
    bool AutomaticSync = false,
    int SyncIntervalMinutes = 60)
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Comic Companion";

    [JsonIgnore]
    public bool IsManaged => InstallationFolder is not null;
    [JsonIgnore]
    public Uri? EffectiveBrowserUrl => BrowserUrl ?? ApiUrl;

    [JsonIgnore]
    public string ManagedPath => $"/managed/comiccompanion/{(Id == Guid.Empty ? ComicCompanionService.LegacyInstanceId : Id):D}";

    public Uri BrowserLink(Uri url) => IsManaged
        ? new Uri($"{ManagedPath}/{EffectiveBrowserUrl!.MakeRelativeUri(url)}", UriKind.Relative)
        : url;
}

public sealed class ComicCompanionService(string settingsPath, ILogger<ComicCompanionService> logger,
    Func<ProcessStartInfo, Process?>? startProcess = null, Func<Process, Task>? stopProcess = null) : BackgroundService
{
    public static readonly Guid LegacyInstanceId = new("7926576f-37c3-462d-a59e-2a6118ca439b");
    private const string ApplicationName = "HydrusComicCompanion";
    private const string SyncVariable = "PSM_COMIC_SYNC_MINUTES";
    private static readonly ManagedBackendStatus NoProcess = new("No PSM-owned process", false);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ManagedBackendStatus> _statuses = new();
    private readonly Dictionary<Guid, Process> _owned = [];
    private bool _stopping;

    public ManagedBackendStatus GetStatus() => GetStatus(LegacyInstanceId);
    public ManagedBackendStatus GetStatus(Guid instanceId) => _statuses.GetValueOrDefault(instanceId, NoProcess);

    public async Task<ComicCompanionSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        (await GetAllAsync(cancellationToken)).FirstOrDefault(settings => settings.Id == LegacyInstanceId) ?? new();

    public async Task<IReadOnlyList<ComicCompanionSettings>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadSettingsAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public Task<ComicCompanionSettings> SaveSettingsAsync(ComicCompanionSettings settings, CancellationToken cancellationToken = default) =>
        SaveSettingsCoreAsync(settings, false, cancellationToken);

    public Task<ComicCompanionSettings> UpdateSettingsAsync(ComicCompanionSettings settings, CancellationToken cancellationToken = default) =>
        SaveSettingsCoreAsync(settings, true, cancellationToken);

    private async Task<ComicCompanionSettings> SaveSettingsCoreAsync(ComicCompanionSettings settings, bool requireExisting, CancellationToken cancellationToken)
    {
        settings = Normalize(settings with { Id = settings.Id == Guid.Empty ? LegacyInstanceId : settings.Id });
        if (settings.ApiUrl is null)
            throw new InvalidDataException("Enter the Comic Companion API base URL.");
        if (settings.IsManaged) CreateStartInfo(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReapExitedProcess(settings.Id);
            if (_owned.ContainsKey(settings.Id))
                throw new InvalidDataException("Stop the PSM-owned process before changing Comic Companion settings.");

            var instances = await ReadSettingsAsync(cancellationToken);
            var index = instances.FindIndex(instance => instance.Id == settings.Id);
            if (requireExisting && index < 0)
                throw new InvalidDataException("The Comic Companion instance no longer exists.");
            if (index < 0) instances.Add(settings);
            else instances[index] = settings;
            await WriteSettingsAsync(instances, cancellationToken);
            _statuses.TryRemove(settings.Id, out _);
            return settings;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveSettingsAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopOwnedAsync(instanceId);
            if (_owned.ContainsKey(instanceId))
                throw new InvalidDataException("Comic Companion could not stop. Its configuration was not removed.");

            var instances = await ReadSettingsAsync(cancellationToken);
            if (instances.RemoveAll(instance => instance.Id == instanceId) > 0)
                await WriteSettingsAsync(instances, cancellationToken);
            _statuses.TryRemove(instanceId, out _);
        }
        finally { _gate.Release(); }
    }

    public Task StartInstanceAsync(CancellationToken cancellationToken = default) => StartInstanceAsync(LegacyInstanceId, cancellationToken);

    public async Task StartInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StartOwnedAsync(instanceId, cancellationToken); }
        finally { _gate.Release(); }
    }

    public Task StopInstanceAsync() => StopInstanceAsync(LegacyInstanceId);

    public async Task StopInstanceAsync(Guid instanceId)
    {
        await _gate.WaitAsync();
        try { await StopOwnedAsync(instanceId); }
        finally { _gate.Release(); }
    }

    public Task RestartInstanceAsync(CancellationToken cancellationToken = default) => RestartInstanceAsync(LegacyInstanceId, cancellationToken);

    public async Task RestartInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReapExitedProcess(instanceId);
            if (!_owned.ContainsKey(instanceId)) return;
            await StopOwnedAsync(instanceId);
            if (!_owned.ContainsKey(instanceId)) await StartOwnedAsync(instanceId, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var settings in await GetAllAsync(stoppingToken))
                if (settings.StartWithApp)
                    await StartInstanceAsync(settings.Id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            logger.LogWarning("Unable to load Comic Companion autostart settings ({ErrorType}).", exception.GetType().Name);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            try
            {
                foreach (var instanceId in _owned.Keys.ToArray())
                    ReapExitedProcess(instanceId);
            }
            finally { _gate.Release(); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            foreach (var instanceId in _owned.Keys.ToArray())
                await StopOwnedAsync(instanceId);
        }
        finally { _gate.Release(); }
        await base.StopAsync(cancellationToken);
    }

    public static ProcessStartInfo CreateStartInfo(ComicCompanionSettings settings)
    {
        settings = Normalize(settings);
        var folder = settings.InstallationFolder ?? throw new InvalidDataException("Comic Companion is not configured for managed launch.");
        var executable = Path.Combine(folder, ApplicationName + ".exe");
        var assembly = Path.Combine(folder, ApplicationName + ".dll");
        var project = Path.Combine(folder, ApplicationName + ".csproj");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = folder,
            UseShellExecute = false
        };
        if (File.Exists(executable))
            start.FileName = executable;
        else if (File.Exists(assembly) && File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
            start.ArgumentList.Add(assembly);
        else if (File.Exists(project))
        {
            foreach (var argument in new[] { "run", "--project", project, "--no-launch-profile", "--" })
                start.ArgumentList.Add(argument);
        }
        else
            throw new InvalidDataException("The installation folder must contain the HydrusComicCompanion project or published application.");

        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(settings.ApiUrl!.AbsoluteUri.TrimEnd('/'));
        start.Environment["PSM_COMIC_PATH_BASE"] = settings.ManagedPath;
        start.Environment.Remove(SyncVariable);
        if (settings.AutomaticSync)
            start.Environment[SyncVariable] = settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        return start;
    }

    private async Task<List<ComicCompanionSettings>> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(settingsPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var legacy = Normalize(document.RootElement.Deserialize<ComicCompanionSettings>()! with { Id = LegacyInstanceId });
                return legacy.ApiUrl is null ? [] : [legacy];
            }

            var instances = document.RootElement.Deserialize<List<ComicCompanionSettings?>>()
                ?? throw new InvalidDataException("Comic Companion settings are empty.");
            var normalized = new List<ComicCompanionSettings>(instances.Count);
            var ids = new HashSet<Guid>();
            foreach (var instance in instances)
            {
                if (instance is null || instance.ApiUrl is null || instance.Id == Guid.Empty || !ids.Add(instance.Id))
                    throw new InvalidDataException("Comic Companion settings contain an invalid or duplicate instance.");
                normalized.Add(Normalize(instance));
            }
            return normalized;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private async Task WriteSettingsAsync(List<ComicCompanionSettings> instances, CancellationToken cancellationToken)
    {
        var temporaryPath = settingsPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, instances, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally { File.Delete(temporaryPath); }
    }

    private static ComicCompanionSettings Normalize(ComicCompanionSettings settings)
    {
        var name = settings.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 60)
            throw new InvalidDataException("Enter a Comic Companion instance name of 1 to 60 characters.");
        settings = settings with { Name = name };
        var apiUrl = NormalizeUrl(settings.ApiUrl);
        var browserUrl = NormalizeUrl(settings.BrowserUrl);
        var folder = string.IsNullOrWhiteSpace(settings.InstallationFolder) ? null : settings.InstallationFolder.Trim();
        if (apiUrl is null && (browserUrl is not null || folder is not null))
            throw new InvalidDataException("Enter the Comic Companion API base URL.");
        if (settings.SyncIntervalMinutes < 1 || settings.SyncIntervalMinutes > int.MaxValue / 60000)
            throw new InvalidDataException("The sync interval must be between 1 and 35791 minutes.");
        if (folder is null)
            return settings with { ApiUrl = apiUrl, BrowserUrl = browserUrl, InstallationFolder = null, StartWithApp = false, AutomaticSync = false };
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Enter an absolute local installation folder.");
        if (apiUrl!.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(apiUrl.IdnHost, out var address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || apiUrl.AbsolutePath != "/")
            throw new InvalidDataException("Managed Comic Companion requires an HTTP base URL with a specific local IP address and port.");
        return settings with { ApiUrl = apiUrl, BrowserUrl = browserUrl, InstallationFolder = Path.GetFullPath(folder) };
    }

    private static Uri? NormalizeUrl(Uri? url)
    {
        if (url is null) return null;
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(url.Host) || url.Port < 1 || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
            throw new InvalidDataException("Use an absolute HTTP or HTTPS base URL without credentials, a query, or a fragment.");
        return new Uri(url.AbsoluteUri.TrimEnd('/') + "/");
    }

    private async Task StartOwnedAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (_stopping) return;
        ReapExitedProcess(instanceId);
        if (_owned.ContainsKey(instanceId)) return;
        try
        {
            var instances = await ReadSettingsAsync(cancellationToken);
            var settings = instances.Find(instance => instance.Id == instanceId);
            if (settings is null || !settings.IsManaged) return;
            var start = CreateStartInfo(settings);
            var port = settings.ApiUrl!.Port;
            if (instances.Any(instance => instance.ApiUrl?.Port == port && _owned.ContainsKey(instance.Id)) ||
                IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == port))
                throw new InvalidDataException("The configured port is already in use. PSM will not take ownership of an existing service.");
            cancellationToken.ThrowIfCancellationRequested();
            _owned.Add(instanceId, (startProcess ?? Process.Start)(start) ?? throw new InvalidDataException("Comic Companion did not start."));
            _statuses[instanceId] = new("Process started (API readiness is not checked)", true);
        }
        catch (InvalidDataException exception) { _statuses[instanceId] = new(exception.Message, false); }
        catch (Exception exception) when (IsSettingsError(exception) || exception is Win32Exception or NetworkInformationException or InvalidOperationException)
        {
            ReportFailure(instanceId, "Unable to start Comic Companion. Check installation, configuration and access permissions.", exception);
        }
    }

    private async Task StopOwnedAsync(Guid instanceId)
    {
        ReapExitedProcess(instanceId);
        if (!_owned.TryGetValue(instanceId, out var process)) return;
        try
        {
            if (!process.HasExited)
            {
                await (stopProcess ?? StopProcessAsync)(process);
            }
            _statuses.TryRemove(instanceId, out _);
            _owned.Remove(instanceId);
            process.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException or AggregateException)
        {
            ReapExitedProcess(instanceId);
            if (_owned.ContainsKey(instanceId))
                ReportFailure(instanceId, "Unable to confirm shutdown. The owned process remains tracked.", exception);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private void ReapExitedProcess(Guid instanceId)
    {
        if (!_owned.TryGetValue(instanceId, out var process) || !process.HasExited) return;
        _statuses[instanceId] = new($"Process exited (code {process.ExitCode}).", false);
        _owned.Remove(instanceId);
        process.Dispose();
    }

    private void ReportFailure(Guid instanceId, string message, Exception exception)
    {
        _statuses[instanceId] = new(message, _owned.ContainsKey(instanceId));
        logger.LogWarning("Comic Companion {InstanceId}: {Message} ({ErrorType}).", instanceId, message, exception.GetType().Name);
    }

    private static bool IsSettingsError(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException;
}

public sealed class ComicCompanionCatalogProbe(HttpClient client)
{
    private const int MaxResponseBytes = 32 * 1024;

    public async Task<bool> IsAvailableAsync(ComicCompanionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.ApiUrl is not { } apiUrl) return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var url = new Uri(apiUrl, "api/manager/comics?q=&limit=1&sort=TitleAscending");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;

            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
            var entries = await response.Content.ReadFromJsonAsync<CatalogEntry[]>(timeout.Token);
            return entries is not null && entries.All(entry => entry is { Id: > 0 } && !string.IsNullOrWhiteSpace(entry.Title));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private sealed record CatalogEntry(int Id, string? Title);
}

public static class ComicCompanionRegistration
{
    public static IServiceCollection AddComicCompanion(this IServiceCollection services, string dataDirectory)
    {
        services.AddHttpClient<ComicCompanionCatalogProbe>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddSingleton(provider => new ComicCompanionService(
            Path.Combine(dataDirectory, "comic-companion.json"), provider.GetRequiredService<ILogger<ComicCompanionService>>()));
        services.AddHostedService(provider => provider.GetRequiredService<ComicCompanionService>());
        return services;
    }
}