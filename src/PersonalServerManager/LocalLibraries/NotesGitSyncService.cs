using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PersonalServerManager.LocalLibraries;

public sealed record NotesGitSyncStatus(string Message, bool IsRunning = false,
    DateTimeOffset? LastSuccess = null, DateTimeOffset? LastAttempt = null);

public sealed class NotesGitSyncService(ILocalLibraryStore libraries, ILogger<NotesGitSyncService> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, NotesGitSyncStatus> _statuses = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public NotesGitSyncStatus GetStatus(Guid instanceId) =>
        _statuses.GetValueOrDefault(instanceId) ?? new("Not synced yet.");

    public async Task PullAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        if (!await _gate.WaitAsync(0, deadline.Token))
        {
            _statuses.AddOrUpdate(instanceId, new NotesGitSyncStatus("Another Notes sync is running."),
                (_, status) => status.IsRunning ? status : status with { Message = "Another Notes sync is running." });
            return;
        }

        var previous = GetStatus(instanceId);
        _statuses[instanceId] = previous with { Message = "Syncing...", IsRunning = true, LastAttempt = DateTimeOffset.UtcNow };
        try
        {
            var instance = (await libraries.GetAllAsync(deadline.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null || instance.Kind != LocalLibraryKind.Notes)
            {
                Complete(instanceId, "Skipped: not a Notes library.");
                return;
            }

            var root = Path.GetFullPath(instance.RootPath);
            var gitEntry = Path.Combine(root, ".git");
            if (!Directory.Exists(root) || !(Directory.Exists(gitEntry) || File.Exists(gitEntry)))
            {
                Complete(instanceId, "Skipped: the Notes folder is not a Git repository.");
                return;
            }

            var topLevel = await GitAsync(root, deadline.Token, "rev-parse", "--show-toplevel");
            if (topLevel.ExitCode != 0 || topLevel.Truncated || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel.Output.Trim())),
                Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                Complete(instanceId, "Skipped: the Notes folder is not a Git repository root.");
                return;
            }

            var tree = await GitAsync(root, deadline.Token, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none");
            if (tree.ExitCode != 0)
            {
                Complete(instanceId, "Sync failed: unable to check the working tree.");
                return;
            }
            if (tree.Truncated || HasChanges(tree.Output))
            {
                Complete(instanceId, "Skipped: the working tree has local changes.");
                return;
            }

            var before = await GitAsync(root, deadline.Token, "rev-parse", "--verify", "HEAD");
            if (before.ExitCode != 0)
            {
                Complete(instanceId, "Sync failed: no committed branch is available.");
                return;
            }
            var pull = await GitAsync(root, deadline.Token, "pull", "--ff-only", "--no-rebase", "--no-autostash");
            if (pull.ExitCode != 0)
            {
                Complete(instanceId, "Sync failed: fast-forward pull unavailable. Check the upstream, access, and branch history.");
                logger.LogWarning("Notes Git sync failed for instance {InstanceId}.", instanceId);
                return;
            }

            var after = await GitAsync(root, deadline.Token, "rev-parse", "--verify", "HEAD");
            if (after.ExitCode != 0)
            {
                Complete(instanceId, "Sync failed: unable to verify the updated branch.");
                return;
            }
            Complete(instanceId, before.Output == after.Output ? "Already up to date." : "Synced successfully.", success: true);
        }
        catch (OperationCanceledException)
        {
            Complete(instanceId, cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested
                ? "Sync canceled." : "Sync timed out.");
        }
        catch (Exception)
        {
            Complete(instanceId, "Sync failed: Git or the Notes folder is unavailable.");
            logger.LogWarning("Notes Git sync could not run for instance {InstanceId}.", instanceId);
        }
        finally { _gate.Release(); }
    }

    private void Complete(Guid instanceId, string message, bool success = false) =>
        _statuses.AddOrUpdate(instanceId, new NotesGitSyncStatus(message), (_, status) => status with
        {
            Message = message, IsRunning = false,
            LastSuccess = success ? DateTimeOffset.UtcNow : status.LastSuccess
        });

    private static bool HasChanges(string porcelain)
    {
        if (porcelain.Length == 0) return false;
        var recordEnd = porcelain.IndexOf('\0');
        if (recordEnd < 4 || porcelain[2] != ' ') return true;
        return porcelain[0] != ' ' || porcelain[1] != ' ' || recordEnd + 1 != porcelain.Length;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            do
            {
                try
                {
                    var instances = await libraries.GetAllAsync(stoppingToken);
                    foreach (var instance in instances.Where(item => item.Kind == LocalLibraryKind.Notes && item.AutoGitPull))
                        await PullAsync(instance.Id, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Automatic Notes Git sync could not read library settings."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        base.Dispose();
    }

    private sealed record GitResult(int ExitCode, string Output, bool Truncated);

    private static async Task<GitResult> GitAsync(string root, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        start.Environment["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes -oStrictHostKeyChecking=yes";
        foreach (var setting in new[] { "credential.interactive=Never", "core.hooksPath=/dev/null", "merge.autostash=false", "rebase.autostash=false" })
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.StandardInput.Close();
        var output = ReadBoundedAsync(process.StandardOutput, token);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await process.WaitForExitAsync(token);
            var captured = await output;
            await error;
            return new(process.ExitCode, captured.Output, captured.Truncated);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException) { }
            }
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            var retained = Math.Min(count, 65536 - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < count;
        }
        return (output.ToString(), truncated);
    }
}