using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PersonalServerManager.Management;

public sealed record ManagedBackendStatus(string Message, bool IsRunning);

public sealed class ManagedBackendService(
    IStashInstanceStore stashStore,
    IHydrusInstanceStore hydrusStore,
    ILogger<ManagedBackendService> logger,
    ISwarmUIInstanceStore? swarmStore = null,
    Func<ProcessStartInfo, Process?>? launchProcess = null) : BackgroundService
{
    private static readonly string SwarmUIExecutable = Path.Combine("src", "bin", "live_release", "SwarmUI.exe");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Process> _owned = [];
    private readonly ConcurrentDictionary<Guid, ManagedBackendStatus> _statuses = new();
    private bool _stopping;

    public ManagedBackendStatus GetStatus(Guid instanceId) =>
        _statuses.GetValueOrDefault(instanceId, new("Stopped", false));

    public static void ValidateStashInstance(StashInstance instance)
    {
        if (!instance.IsManaged) return;
        ValidateFolder(instance.InstallationFolder!, "stash-win.exe");
        ValidatePort(instance.Port);
        var root = new Uri($"http://127.0.0.1:{instance.Port}/");
        if (instance.WebUiUrl != root || instance.GraphQlEndpoint != new Uri(root, "graphql"))
            throw new InvalidDataException("Managed Stash URLs must match http://127.0.0.1:{port}/ and /graphql.");
    }

    public static void ValidateHydrusInstance(HydrusInstance instance)
    {
        if (!instance.IsManaged) return;
        ValidateFolder(instance.InstallationFolder!, "hydrus_client.exe");
        HydrusClient.ValidateEndpoint(instance.ApiEndpoint);
        if (string.IsNullOrWhiteSpace(instance.ApiKey))
            throw new InvalidDataException("Managed Hydrus requires a user-configured API URL and API key.");
    }

    public static void ValidateSwarmUIInstance(SwarmUIInstance instance)
    {
        if (!instance.IsManaged) return;
        ValidateFolder(instance.InstallationFolder!, SwarmUIExecutable);
        ValidatePort(instance.Port);
        if (instance.BaseUrl != new Uri($"http://127.0.0.1:{instance.Port}/"))
            throw new InvalidDataException("Managed SwarmUI URL must match http://127.0.0.1:{port}/.");
    }

    public static string UpdateStashConfigPort(string yaml, int port)
    {
        ValidatePort(port);
        var document = new YamlStream();
        try
        {
            document.Load(new StringReader(yaml));
            if (document.Documents.Count == 0)
                document.Add(new YamlDocument(new YamlMappingNode()));
            if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("Stash config.yml must contain one YAML mapping.");
            root.Children[new YamlScalarNode("port")] = new YamlScalarNode(port.ToString(CultureInfo.InvariantCulture));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            document.Save(writer, false);
            return writer.ToString();
        }
        catch (YamlException)
        {
            throw new InvalidDataException("Stash config.yml is invalid YAML. It was left untouched.");
        }
    }

    public async Task StartInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StartInstanceLockedAsync(instanceId, cancellationToken); }
        finally { _gate.Release(); }
    }

    public Task UpdateInstanceAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
        UpdateInstanceLockedAsync(instance.Id, "Stash", () => stashStore.UpdateAsync(instance, cancellationToken), cancellationToken);

    public Task UpdateInstanceAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
        UpdateInstanceLockedAsync(instance.Id, "Hydrus", () => hydrusStore.UpdateAsync(instance, cancellationToken), cancellationToken);

    public Task UpdateInstanceAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) =>
        UpdateInstanceLockedAsync(instance.Id, "SwarmUI", () => (swarmStore ?? throw new InvalidDataException("The selected backend instance no longer exists.")).UpdateAsync(instance, cancellationToken), cancellationToken);

    private async Task UpdateInstanceLockedAsync(Guid instanceId, string backend, Func<Task> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_owned.TryGetValue(instanceId, out var process) && !process.HasExited)
                throw new InvalidDataException($"Stop {backend} before editing its configuration.");
            await update();
        }
        finally { _gate.Release(); }
    }

    private async Task StartInstanceLockedAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            SetStatus(instanceId, "PSM is shutting down.");
            return;
        }
        if (_owned.TryGetValue(instanceId, out var owned))
        {
            if (!owned.HasExited) return;
            owned.Dispose();
            _owned.Remove(instanceId);
        }
        SetStatus(instanceId, "Starting");
        try
        {
            var stash = (await stashStore.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == instanceId);
            var hydrus = (await hydrusStore.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == instanceId);
            var swarm = swarmStore is null ? null
                : (await swarmStore.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == instanceId);
            if ((stash is null ? 0 : 1) + (hydrus is null ? 0 : 1) + (swarm is null ? 0 : 1) > 1)
                throw new InvalidDataException("The instance ID is shared by two backends.");
            string folder;
            string executable;
            Uri endpoint;
            IEnumerable<string> arguments = [];
            if (stash is not null && stash.IsManaged)
            {
                ValidateStashInstance(stash);
                folder = Path.GetFullPath(stash.InstallationFolder!);
                executable = Path.Combine(folder, "stash-win.exe");
                endpoint = stash.WebUiUrl;
            }
            else if (hydrus is not null && hydrus.IsManaged)
            {
                ValidateHydrusInstance(hydrus);
                folder = Path.GetFullPath(hydrus.InstallationFolder!);
                executable = Path.Combine(folder, "hydrus_client.exe");
                endpoint = hydrus.ApiEndpoint;
            }
            else if (swarm is not null && swarm.IsManaged)
            {
                ValidateSwarmUIInstance(swarm);
                folder = Path.GetFullPath(swarm.InstallationFolder!);
                executable = Path.Combine(folder, SwarmUIExecutable);
                endpoint = swarm.BaseUrl;
                arguments = ["--launch_mode", "none", "--host", swarm.ListenOnLocalNetwork ? "0.0.0.0" : "127.0.0.1", "--port", swarm.Port.ToString(CultureInfo.InvariantCulture)];
            }
            else
            {
                throw new InvalidDataException("No managed backend exists for this instance.");
            }
            EnsureExecutableStopped(executable);
            EnsureLocalPortAvailable(endpoint);
            cancellationToken.ThrowIfCancellationRequested();
            if (stash is not null)
                await WriteStashConfigAsync(folder, stash.Port, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = folder, UseShellExecute = false };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            if (swarm is not null)
            {
                startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
                startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            }
            var process = (launchProcess is null ? Process.Start(startInfo) : launchProcess(startInfo))
                ?? throw new InvalidDataException("The backend process could not be started.");
            _owned.Add(instanceId, process);
            SetStatus(instanceId, "Running (API readiness is checked separately)", true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(instanceId, "Start cancelled.");
            throw;
        }
        catch (InvalidDataException exception)
        {
            SetStatus(instanceId, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception
            or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            SetStatus(instanceId, "Unable to start backend. Check installation, configuration and access permissions.");
            logger.LogWarning("Managed backend {InstanceId} failed to start ({ErrorType}).", instanceId, exception.GetType().Name);
        }
    }

    public async Task StopInstanceAsync(Guid instanceId)
    {
        await _gate.WaitAsync();
        try { await StopOwnedAsync(instanceId); }
        finally { _gate.Release(); }
    }

    public async Task<bool> RestartInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopOwnedAsync(instanceId);
            if (_owned.ContainsKey(instanceId)) return false;
            await StartInstanceLockedAsync(instanceId, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveInstanceAsync(string backend, Guid instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stash = (await stashStore.GetAllAsync(cancellationToken)).Any(item => item.Id == instanceId);
            var hydrus = (await hydrusStore.GetAllAsync(cancellationToken)).Any(item => item.Id == instanceId);
            var swarm = swarmStore is not null && (await swarmStore.GetAllAsync(cancellationToken)).Any(item => item.Id == instanceId);
            if ((stash ? 1 : 0) + (hydrus ? 1 : 0) + (swarm ? 1 : 0) > 1)
                throw new InvalidDataException("The instance ID is shared by two backends.");
            if (!(backend switch { "Stash" => stash, "Hydrus" => hydrus, "SwarmUI" => swarm, _ => false }))
                throw new InvalidDataException("The selected backend instance no longer exists.");
            await StopOwnedAsync(instanceId);
            if (_owned.ContainsKey(instanceId))
                throw new InvalidDataException(GetStatus(instanceId).Message);
            switch (backend)
            {
                case "Stash": await stashStore.RemoveAsync(instanceId, cancellationToken); break;
                case "Hydrus": await hydrusStore.RemoveAsync(instanceId, cancellationToken); break;
                case "SwarmUI": await swarmStore!.RemoveAsync(instanceId, cancellationToken); break;
            }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stash = await stashStore.GetAllAsync(stoppingToken);
            var hydrus = await hydrusStore.GetAllAsync(stoppingToken);
            var swarm = await GetSwarmUIInstancesForAutostartAsync(stoppingToken);
            foreach (var id in stash.Where(item => item.IsManaged && item.StartWithApp).Select(item => item.Id)
                .Concat(hydrus.Where(item => item.IsManaged && item.StartWithApp).Select(item => item.Id))
                .Concat(swarm.Where(item => item.IsManaged && item.StartWithApp).Select(item => item.Id)).Distinct())
                await StartInstanceAsync(id, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    foreach (var entry in _owned.ToArray())
                    {
                        if (!entry.Value.HasExited) continue;
                        SetStatus(entry.Key, $"Exited (code {entry.Value.ExitCode}).");
                        entry.Value.Dispose();
                        _owned.Remove(entry.Key);
                    }
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Managed backend monitoring could not continue ({ErrorType}).", exception.GetType().Name);
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                _stopping = true;
                foreach (var id in _owned.Keys.ToArray()) await StopOwnedAsync(id);
            }
            finally { _gate.Release(); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _stopping = true;
            foreach (var id in _owned.Keys.ToArray()) await StopOwnedAsync(id);
        }
        finally { _gate.Release(); }
        await base.StopAsync(cancellationToken);
    }

    private async Task StopOwnedAsync(Guid instanceId)
    {
        if (!_owned.TryGetValue(instanceId, out var process))
        {
            SetStatus(instanceId, "Stopped (no PSM-owned process)");
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                SetStatus(instanceId, "Stopping", true);
                try { process.CloseMainWindow(); }
                catch (Win32Exception) { }
                using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(gracefulTimeout.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(killTimeout.Token);
                }
            }
            _owned.Remove(instanceId);
            process.Dispose();
            SetStatus(instanceId, "Stopped");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            SetStatus(instanceId, "Unable to confirm shutdown. The owned process remains tracked.", true);
            logger.LogWarning("Managed backend {InstanceId} shutdown failed ({ErrorType}).", instanceId, exception.GetType().Name);
        }
    }

    private void SetStatus(Guid id, string message, bool running = false) => _statuses[id] = new(message, running);

    private async Task<IReadOnlyList<SwarmUIInstance>> GetSwarmUIInstancesForAutostartAsync(CancellationToken cancellationToken)
    {
        if (swarmStore is null) return [];
        try { return await swarmStore.GetAllAsync(cancellationToken); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("SwarmUI autostart skipped ({ErrorType}).", exception.GetType().Name);
            return [];
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new InvalidDataException("Port must be between 1 and 65535.");
    }

    private static void ValidateFolder(string folder, string executable)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder) || !File.Exists(Path.Combine(folder, executable)))
            throw new InvalidDataException($"Installation folder must be absolute and contain {executable}.");
    }

    private static void EnsureExecutableStopped(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null)
                        throw new InvalidDataException("Cannot verify that this installation is stopped.");
                    if (string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("This installation is already running. PSM will not take ownership.");
                }
                catch (Win32Exception)
                {
                    throw new InvalidDataException("Cannot verify that this installation is stopped. Close other backend processes first.");
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static void EnsureLocalPortAvailable(Uri endpoint)
    {
        if (!endpoint.IsLoopback) return;
        var address = endpoint.Host == "[::1]" ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        var listener = new TcpListener(address, endpoint.Port) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException) { throw new InvalidDataException("The API port is already in use. PSM will not take ownership."); }
        finally { listener.Stop(); }
    }

    private static async Task WriteStashConfigAsync(string folder, int port, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, "config.yml");
        var yaml = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
        var updated = UpdateStashConfigPort(yaml, port);
        var temporary = Path.Combine(folder, $"config.yml.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, updated, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}