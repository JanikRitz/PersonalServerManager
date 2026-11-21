using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalServerManager.LocalLibraries;

public sealed record ManagedCalibreSettings(int Port = 8080, bool StartWithApp = false);

public sealed record ManagedCalibreStatus(string Message, Uri? Url = null);

public sealed class ManagedCalibreServer(string settingsPath, ILocalLibraryStore libraries, ILogger<ManagedCalibreServer> logger)
    : BackgroundService
{
    private sealed record LibraryInfo([property: JsonPropertyName("library_map")] Dictionary<string, string>? LibraryMap);
    private sealed record RunningServer(Process Process, Uri Url, Dictionary<string, string> LibraryIds);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2) };
    private volatile RunningServer? _running;
    private volatile string _message = "Stopped";

    public ManagedCalibreStatus GetStatus() => _running is { } running && IsAlive(running.Process)
        ? new("Running", running.Url)
        : new(_running is null ? _message : "Calibre exited unexpectedly.");

    public LocalLibraryInstance WithReaderConnection(LocalLibraryInstance instance) =>
        instance.ContentServerUrl is null && instance.Kind is (LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic) &&
        _running is { } running && IsAlive(running.Process) &&
        running.LibraryIds.TryGetValue(LocalLibraryPaths.NormalizeRoot(instance.RootPath), out var libraryId)
            ? instance with { ContentServerUrl = running.Url, ContentServerLibraryId = libraryId }
            : instance;

    public async Task<ManagedCalibreSettings> GetSettingsAsync(CancellationToken token = default)
    {
        if (!File.Exists(settingsPath)) return new();
        await using var file = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var settings = await JsonSerializer.DeserializeAsync<ManagedCalibreSettings>(file, JsonOptions, token)
            ?? throw new InvalidDataException("Calibre server settings are missing.");
        Validate(settings);
        return settings;
    }

    public async Task SaveSettingsAsync(ManagedCalibreSettings settings, CancellationToken token = default)
    {
        Validate(settings);
        await _gate.WaitAsync(token);
        try
        {
            if (GetStatus().Url is not null) throw new InvalidDataException("Stop Calibre before changing its settings.");
            var temporary = settingsPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, JsonOptions), token);
                File.Move(temporary, settingsPath, overwrite: true);
                _message = "Stopped";
            }
            finally { File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    public async Task StartServerAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        Process? process = null;
        try
        {
            if (GetStatus().Url is not null) return;
            _running?.Process.Dispose();
            _running = null;
            var settings = await GetSettingsAsync(token);
            var roots = (await libraries.GetAllAsync(token))
                .Where(library => library.Kind is LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic)
                .Select(library => LocalLibraryPaths.Root(library.RootPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 0) throw new InvalidDataException("Add a Calibre library before starting the server.");
            if (roots.Any(root => !File.Exists(LocalLibraryPaths.Resolve(root, "metadata.db"))))
                throw new InvalidDataException("Every managed Calibre library must contain metadata.db.");
            if (roots.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
                throw new InvalidDataException("Managed Calibre library folders must have distinct names.");
            var portProbe = new TcpListener(IPAddress.Loopback, settings.Port) { ExclusiveAddressUse = true };
            try { portProbe.Start(); }
            catch (SocketException) { throw new InvalidDataException("The configured Calibre port is already in use or unavailable."); }
            finally { portProbe.Stop(); }
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2", "calibre-server.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("Install Calibre in its standard Program Files location first.");

            _message = "Starting Calibre...";
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--listen-on", "127.0.0.1", "--port", settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--disable-fallback-to-detected-interface", "--disable-local-write", "--disable-use-bonjour", "--disable-auth" }.Concat(roots))
                start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidDataException("Calibre could not be launched.");

            var url = new Uri($"http://127.0.0.1:{settings.Port}/");
            var ready = await WaitForLibrariesAsync(process, url, roots, token);
            _running = new(process, url, ready);
            process = null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            _message = exception is InvalidDataException ? exception.Message : "Calibre could not start. Check its settings and library access.";
            logger.LogWarning("Managed Calibre startup failed: {Message}", _message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _message = "Stopped"; }
        finally
        {
            try { if (process is not null) await TerminateAsync(process); }
            finally { _gate.Release(); }
        }
    }

    public async Task StopServerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_running is { } running) await TerminateAsync(running.Process);
            _running = null;
            _message = "Stopped";
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if ((await GetSettingsAsync(stoppingToken)).StartWithApp)
                await StartServerAsync(stoppingToken);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            _message = "Calibre settings could not be loaded.";
            logger.LogWarning("{Message}", _message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await StopServerAsync();
    }

    public override void Dispose()
    {
        base.Dispose();
        _http.Dispose();
    }

    private async Task<Dictionary<string, string>> WaitForLibrariesAsync(Process process, Uri url, string[] roots, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (!IsAlive(process)) throw new InvalidDataException("Calibre exited before its libraries were ready.");
                LibraryInfo? response = null;
                try { response = await _http.GetFromJsonAsync<LibraryInfo>(new Uri(url, "ajax/library-info"), limit.Token); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!limit.IsCancellationRequested) { }
                if (response?.LibraryMap is { } map && IsAlive(process))
                {
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var root in roots)
                    {
                        var ids = map.Where(pair => string.Equals(pair.Value, Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (ids.Length != 1 || result.ContainsValue(ids[0].Key))
                            throw new InvalidDataException("Calibre could not uniquely match the configured folders to server library IDs.");
                        result.Add(root, ids[0].Key);
                    }
                    return result;
                }
                await Task.Delay(250, limit.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidDataException("Calibre did not become ready within 20 seconds.");
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        if (IsAlive(process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (!IsAlive(process)) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        process.Dispose();
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static void Validate(ManagedCalibreSettings settings)
    {
        if (settings.Port is < 1024 or > 65535) throw new InvalidDataException("Enter a port between 1024 and 65535.");
    }

    private static bool IsExpected(Exception exception) => exception is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or JsonException or Win32Exception;
}