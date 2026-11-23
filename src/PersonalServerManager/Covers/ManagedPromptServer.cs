using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PersonalServerManager.Covers;

public sealed record ManagedPromptServerSettings(string ExecutablePath = "", string Arguments = "",
    string WorkingDirectory = "", bool StartWithApp = false);

public sealed record ManagedPromptServerStatus(string Message, bool IsRunning = false);

public sealed class ManagedPromptServer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly ILogger<ManagedPromptServer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private Process? _process;
    private ManagedPromptServerStatus _status = new("Stopped");
    private volatile bool _shutdown;

    public ManagedPromptServer(string settingsPath, ILogger<ManagedPromptServer> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsPath = Path.GetFullPath(settingsPath);
        _logger = logger;
    }

    public ManagedPromptServerStatus Status
    {
        get
        {
            lock (_processLock)
            {
                if (_status.IsRunning && _process is not null && _process.HasExited)
                    _status = new("Prompt server exited.");
                return _status;
            }
        }
    }

    public async Task<ManagedPromptServerSettings> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadConfigurationAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveConfigurationAsync(ManagedPromptServerSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (json.Length > 64 * 1024) throw new ArgumentException("The managed prompt server configuration is too large.", nameof(settings));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status.IsRunning) throw new InvalidOperationException("Stop the prompt server before changing its configuration.");
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(json, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _settingsPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    public async Task StartServerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_shutdown) throw new InvalidOperationException("The prompt server manager is shutting down.");
            if (Status.IsRunning) return;
            var settings = await ReadConfigurationAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !Path.IsPathFullyQualified(settings.ExecutablePath) ||
                !File.Exists(settings.ExecutablePath))
                throw new InvalidDataException("The prompt server executable must be an existing absolute file path.");
            if (settings.WorkingDirectory.Length > 0 &&
                (!Path.IsPathFullyQualified(settings.WorkingDirectory) || !Directory.Exists(settings.WorkingDirectory)))
                throw new InvalidDataException("The working directory must be an existing absolute directory path.");
            if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(settings.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(settings.ExecutablePath), ".com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The prompt server executable must be an .exe or .com file.");
            var startInfo = new ProcessStartInfo(settings.ExecutablePath)
            {
                Arguments = settings.Arguments,
                WorkingDirectory = settings.WorkingDirectory.Length == 0 ? Path.GetDirectoryName(settings.ExecutablePath)! : settings.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            cancellationToken.ThrowIfCancellationRequested();
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The prompt server could not be started.");
            lock (_processLock)
            {
                _process?.Dispose();
                _process = process;
                _status = new("Running", true);
                process.Exited += OnProcessExited;
                process.EnableRaisingEvents = true;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or JsonException or ArgumentException)
        {
            if (!_shutdown)
            {
                lock (_processLock) _status = new("Prompt server could not start. Check its configuration.");
                _logger.LogWarning("Managed prompt server could not start; check its configuration and executable permissions.");
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopServerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopOwnedProcessAsync(); }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if ((await GetConfigurationAsync(stoppingToken)).StartWithApp)
                await StartServerAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or JsonException or ArgumentException)
        {
            if (!_shutdown)
            {
                lock (_processLock) _status = new("Prompt server could not start. Check its configuration.");
                _logger.LogWarning("Managed prompt server automatic startup failed; check its configuration.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _shutdown = true;
            await StopOwnedProcessAsync();
        }
        finally { _gate.Release(); }
        await base.StopAsync(cancellationToken);
    }

    private async Task<ManagedPromptServerSettings> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath)) return new();
        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            if (stream.Length > 64 * 1024) throw new InvalidDataException();
            var settings = await JsonSerializer.DeserializeAsync<ManagedPromptServerSettings>(stream, JsonOptions, cancellationToken);
            Validate(settings);
            return settings!;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException("The managed prompt server configuration is invalid. It was left untouched.");
        }
    }

    private static void Validate(ManagedPromptServerSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ExecutablePath is null || settings.WorkingDirectory is null || settings.Arguments is null ||
            settings.ExecutablePath.Length > 4096 || settings.WorkingDirectory.Length > 4096 || settings.Arguments.Length > 16384 ||
            settings.ExecutablePath.Any(char.IsControl) || settings.WorkingDirectory.Any(char.IsControl) || settings.Arguments.Contains('\0'))
            throw new ArgumentException("The managed prompt server configuration is invalid.", nameof(settings));
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        lock (_processLock)
        {
            if (ReferenceEquals(_process, sender)) _status = new("Prompt server exited.");
        }
    }

    private async Task StopOwnedProcessAsync()
    {
        Process? process;
        lock (_processLock) process = _process;
        if (process is null) { lock (_processLock) _status = new("Stopped"); return; }
        try
        {
            if (!process.HasExited)
            {
                if (process.CloseMainWindow())
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                }
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
            lock (_processLock)
            {
                _process = null;
                _status = new("Stopped");
                process.Dispose();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            lock (_processLock) _status = new("Prompt server could not stop. Retry stopping it.", true);
            _logger.LogWarning("Managed prompt server could not stop.");
            throw;
        }
    }
}