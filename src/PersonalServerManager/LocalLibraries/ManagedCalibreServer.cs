using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PersonalServerManager.LocalLibraries;

public sealed record ManagedCalibreSettings(int Port = 8080, bool StartWithApp = false);

public sealed record ManagedCalibreStatus(string Message, Uri? Url = null, bool IsRunning = false);

/// <summary>calibredb <c>--with-library</c> URL and read-write account of a running managed server.</summary>
public sealed record CalibreWriterConnection(string LibraryUrl, string Username, string Password);

public sealed class ManagedCalibreServer(string settingsPath, ILocalLibraryStore libraries, ILogger<ManagedCalibreServer> logger,
    HttpClient? readinessClient = null, Func<ProcessStartInfo, Process?>? launchProcess = null,
    Func<Process, Task>? terminateProcess = null)
    : BackgroundService
{
    private sealed record LibraryInfo([property: JsonPropertyName("library_map")] Dictionary<string, string>? LibraryMap);
    private sealed record RunningServer(Process Process, Uri Url, IReadOnlyList<string> Roots, IReadOnlyList<string> LibraryIds,
        string ReaderAuthorization, string WriterPassword);

    private const string ReaderUser = "psm-reader";
    private const string WriterUser = "psm-writer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private bool _shutdown;
    private readonly HttpClient _http = readinessClient ?? new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentDictionary<Guid, RunningServer> _running = new();
    private readonly ConcurrentDictionary<Guid, string> _messages = new();
    private volatile string _message = "Stopped";

    public ManagedCalibreStatus GetStatus() => _running.Keys.FirstOrDefault() is var id && id != Guid.Empty
        ? GetStatus(id) : new(_message);

    public ManagedCalibreStatus GetStatus(Guid id) => _running.TryGetValue(id, out var running)
        ? _messages.GetValueOrDefault(id, "Starting Calibre...") is var message && message != "Running"
            ? new(message, IsRunning: IsAlive(running.Process))
            : IsAlive(running.Process) ? new("Running", running.Url, true) : new("Calibre exited unexpectedly.")
        : new(_messages.GetValueOrDefault(id, "Stopped"));

    /// <summary>Returns the instance connected to its running server, using the Calibre library ID of folder <paramref name="folder"/>.</summary>
    public LocalLibraryInstance WithReaderConnection(LocalLibraryInstance instance, int folder = 0) =>
        TryGetReady(instance, folder, out var running)
            ? instance with { ContentServerUrl = running.Url, ContentServerLibraryId = running.LibraryIds[folder] }
            : instance;

    /// <summary>Returns the Authorization header value of the read-only account of the instance's running server.</summary>
    public string? GetReaderAuthorization(LocalLibraryInstance instance) =>
        TryGetReady(instance, 0, out var running) ? running.ReaderAuthorization : null;

    public CalibreWriterConnection? GetWriterConnection(LocalLibraryInstance instance, int folder) =>
        TryGetReady(instance, folder, out var running)
            ? new(running.Url.AbsoluteUri + "#" + running.LibraryIds[folder], WriterUser, running.WriterPassword)
            : null;

    private bool TryGetReady(LocalLibraryInstance instance, int folder, [NotNullWhen(true)] out RunningServer? running) =>
        _running.TryGetValue(instance.Id, out running) && instance.IsManaged &&
        _messages.GetValueOrDefault(instance.Id) == "Running" && IsAlive(running.Process) &&
        folder >= 0 && folder < running.LibraryIds.Count &&
        running.Roots.SequenceEqual(instance.RootPaths.Select(LocalLibraryPaths.NormalizeRoot), StringComparer.OrdinalIgnoreCase);

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
        try
        {
            var library = (await libraries.GetAllAsync(token)).FirstOrDefault(library => library.IsManaged);
            if (library is null) { _message = "Add a Calibre library before starting the server."; return; }
            var settings = await GetSettingsAsync(token);
            await StartLibraryLockedAsync(library with { ManagedPort = settings.Port }, token);
        }
        finally { _gate.Release(); }
    }

    public async Task StartServerAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await StartServerLockedAsync(id, token); }
        finally { _gate.Release(); }
    }

    private async Task StartServerLockedAsync(Guid id, CancellationToken token)
    {
        var library = (await libraries.GetAllAsync(token)).FirstOrDefault(library => library.Id == id);
        if (library is null || !library.IsManaged)
        {
            _messages[id] = "Select a managed Calibre library.";
            return;
        }
        await StartLibraryLockedAsync(library, token);
    }

    private async Task StartLibraryLockedAsync(LocalLibraryInstance library, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        Process? process = null;
        try
        {
            if (_shutdown || _stopping.IsCancellationRequested) return;
            token = lifetime.Token;
            token.ThrowIfCancellationRequested();
            if (_running.TryGetValue(library.Id, out var owned) && IsAlive(owned.Process)) return;
            if (_running.TryRemove(library.Id, out var previous)) previous.Process.Dispose();
            Validate(new(library.ManagedPort));
            var roots = library.RootPaths.Select(LocalLibraryPaths.Root).ToArray();
            if (_running.Values.Any(running => IsAlive(running.Process) &&
                running.Roots.Intersect(roots, StringComparer.OrdinalIgnoreCase).Any()))
                throw new InvalidDataException("This Calibre library already has a managed server.");
            if (roots.Any(root => !File.Exists(LocalLibraryPaths.Resolve(root, "metadata.db"))))
                throw new InvalidDataException("Every managed Calibre library must contain metadata.db.");
            var portProbe = new TcpListener(IPAddress.Loopback, library.ManagedPort) { ExclusiveAddressUse = true };
            try { portProbe.Start(); }
            catch (SocketException) { throw new InvalidDataException("The configured Calibre port is already in use or unavailable."); }
            finally { portProbe.Stop(); }
            var folder = library.CalibreInstallationFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2");
            var executable = LocalLibraryPaths.Resolve(LocalLibraryPaths.Root(folder), "calibre-server.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("The Calibre installation folder must contain calibre-server.exe.");

            _message = "Starting Calibre...";
            _messages[library.Id] = _message;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            var configuration = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "managed-calibre", library.Id.ToString());
            Directory.CreateDirectory(configuration);
            var readerPassword = NewPassword();
            var writerPassword = NewPassword();
            var userDatabase = Path.Combine(configuration, "server-users.sqlite");
            await WriteUserDatabaseAsync(userDatabase, readerPassword, writerPassword, token);
            var readerAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ReaderUser}:{readerPassword}"));
            start.Environment["CALIBRE_CONFIG_DIRECTORY"] = configuration;
            start.Environment["CALIBRE_NO_SI_DANGER_DANGER"] = "1";
            foreach (var argument in new[] { "--listen-on", "127.0.0.1", "--port", library.ManagedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"--url-prefix=/managed/calibre/{library.Id:D}",
                "--disable-fallback-to-detected-interface", "--disable-local-write", "--disable-use-bonjour",
                "--enable-auth", "--auth-mode=basic", "--userdb", userDatabase }.Concat(roots))
                start.ArgumentList.Add(argument);
            token.ThrowIfCancellationRequested();
            process = (launchProcess is null ? Process.Start(start) : launchProcess(start))
                ?? throw new InvalidDataException("Calibre could not be launched.");

            var url = new Uri($"http://127.0.0.1:{library.ManagedPort}/managed/calibre/{library.Id:D}/");
            _running[library.Id] = new(process, url, roots, [], readerAuthorization, writerPassword);
            var ready = await WaitForLibrariesAsync(process, url, readerAuthorization, roots, token);
            token.ThrowIfCancellationRequested();
            _running[library.Id] = new(process, url, roots, roots.Select(root => ready[root]).ToArray(), readerAuthorization, writerPassword);
            _messages[library.Id] = "Running";
            process = null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            _message = exception is InvalidDataException ? exception.Message : "Calibre could not start. Check its settings and library access.";
            _messages[library.Id] = _message;
            logger.LogWarning("Managed Calibre startup failed: {Message}", _message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _message = "Stopped"; _messages[library.Id] = _message; }
        finally
        {
            if (process is not null) await StopOwnedLibraryAsync(library.Id, clearStatus: false);
        }
    }

    public async Task StopServerAsync()
    {
        _stopping.Cancel();
        await _gate.WaitAsync();
        try
        {
            _shutdown = true;
            foreach (var id in _running.Keys) await StopOwnedLibraryAsync(id);
            _message = _running.IsEmpty ? "Stopped" : "Calibre could not stop. Retry stopping the affected libraries.";
        }
        finally { _gate.Release(); }
    }

    public async Task StopServerAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            await StopOwnedLibraryAsync(id);
        }
        finally { _gate.Release(); }
    }

    public async Task RestartServerAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await StopOwnedLibraryAsync(id);
            if (_running.ContainsKey(id)) throw new InvalidDataException(_messages[id]);
            await StartServerLockedAsync(id, token);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveLibraryAsync(Guid id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var library = (await libraries.GetAllAsync(token)).FirstOrDefault(item => item.Id == id);
            if (library is null || !library.IsCalibre)
                throw new InvalidDataException("The selected Calibre library no longer exists.");
            await StopOwnedLibraryAsync(id);
            if (_running.ContainsKey(id)) throw new InvalidDataException(_messages[id]);
            await libraries.RemoveAsync(id, token);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateLibraryAsync(LocalLibraryInstance library, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_running.TryGetValue(library.Id, out var running) && IsAlive(running.Process))
                throw new InvalidDataException("Stop Calibre before editing its configuration.");
            await libraries.UpdateAsync(library, token);
        }
        finally { _gate.Release(); }
    }

    private async Task StopOwnedLibraryAsync(Guid id, bool clearStatus = true)
    {
        if (_running.TryGetValue(id, out var running))
        {
            try
            {
                if (terminateProcess is null) await TerminateAsync(running.Process);
                else await terminateProcess(running.Process);
                if (IsAlive(running.Process)) throw new InvalidOperationException("Calibre is still running.");
                _running.TryRemove(id, out _);
                running.Process.Dispose();
            }
            catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or OperationCanceledException)
            {
                _message = "Calibre could not stop. Retry stopping this library.";
                _messages[id] = _message;
                logger.LogWarning("Managed Calibre termination failed for {LibraryId}: {Message}", id, _message);
                return;
            }
        }
        if (clearStatus) { _message = "Stopped"; _messages[id] = _message; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var library in (await libraries.GetAllAsync(stoppingToken)).Where(library => library.IsManaged && library.StartWithApp))
                await StartServerAsync(library.Id, stoppingToken);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            _message = "Calibre settings could not be loaded.";
            logger.LogWarning("{Message}", _message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        StopServerAsync().GetAwaiter().GetResult();
        base.Dispose();
        if (readinessClient is null) _http.Dispose();
    }

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Recreates the server's user database with fresh per-start reader and writer accounts.</summary>
    private static async Task WriteUserDatabaseAsync(string path, string readerPassword, string writerPassword, CancellationToken token)
    {
        File.Delete(path);
        File.Delete(path + "-journal");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        // Calibre's version 1 user schema (calibre.srv.users); calibre migrates it to newer versions on open.
        command.CommandText = """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                pw TEXT NOT NULL,
                timestamp TEXT DEFAULT CURRENT_TIMESTAMP,
                session_data TEXT NOT NULL DEFAULT "{}",
                restriction TEXT NOT NULL DEFAULT "{}",
                readonly TEXT NOT NULL DEFAULT "n",
                misc_data TEXT NOT NULL DEFAULT "{}",
                UNIQUE(name)
            );
            INSERT INTO users (name, pw, readonly, misc_data) VALUES
                ($reader, $readerPassword, 'y', $misc), ($writer, $writerPassword, 'n', $misc);
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$reader", ReaderUser);
        command.Parameters.AddWithValue("$readerPassword", readerPassword);
        command.Parameters.AddWithValue("$writer", WriterUser);
        command.Parameters.AddWithValue("$writerPassword", writerPassword);
        command.Parameters.AddWithValue("$misc", "{\"allow_change_password_via_http\": false}");
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<Dictionary<string, string>> WaitForLibrariesAsync(Process process, Uri url, string authorization, string[] roots,
        CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (!IsAlive(process)) throw new InvalidDataException("Calibre exited before its libraries were ready.");
                LibraryInfo? response = null;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "ajax/library-info"));
                    request.Headers.TryAddWithoutValidation("Authorization", authorization);
                    using var reply = await _http.SendAsync(request, limit.Token);
                    reply.EnsureSuccessStatusCode();
                    response = await reply.Content.ReadFromJsonAsync<LibraryInfo>(limit.Token);
                }
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
        UnauthorizedAccessException or ArgumentException or JsonException or Win32Exception or SqliteException;
}