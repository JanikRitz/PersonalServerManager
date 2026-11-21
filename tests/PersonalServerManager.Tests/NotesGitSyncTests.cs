using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class NotesGitSyncTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-notes-git-").FullName;

    [Fact]
    public async Task CleanRepositoryFastForwardsAndReportsUpToDate()
    {
        var (source, clone) = await CreateRemoteAsync();
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), "new note");
        await GitAsync(source, "add", ".");
        await GitAsync(source, "commit", "-m", "Update note");
        await GitAsync(source, "push");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.Equal("new note", await File.ReadAllTextAsync(Path.Combine(clone, "note.md")));
        var status = service.GetStatus(instance.Id);
        Assert.Equal("Synced successfully.", status.Message);
        Assert.False(status.IsRunning);
        Assert.NotNull(status.LastAttempt);
        Assert.NotNull(status.LastSuccess);
        await service.PullAsync(instance.Id);
        Assert.Equal("Already up to date.", service.GetStatus(instance.Id).Message);
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("unstaged")]
    [InlineData("untracked")]
    [InlineData("tracked-ignored")]
    public async Task DirtyRepositoryIsSkippedWithoutChangingFilesOrIndex(string change)
    {
        var (source, clone) = await CreateRemoteAsync();
        if (change == "tracked-ignored")
        {
            await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "note.md\n");
            await GitAsync(source, "add", ".gitignore");
            await GitAsync(source, "commit", "-m", "Ignore already tracked note");
            await GitAsync(source, "push");
            await GitAsync(clone, "pull", "--ff-only");
        }
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), "remote change");
        await GitAsync(source, "add", "-u");
        await GitAsync(source, "commit", "-m", "Remote change");
        await GitAsync(source, "push");
        var filename = change == "untracked" ? "untracked note.md" : "note.md";
        await File.WriteAllTextAsync(Path.Combine(clone, filename), "local change");
        if (change == "staged") await GitAsync(clone, "add", filename);
        var head = await GitAsync(clone, "rev-parse", "HEAD");
        var tree = await GitAsync(clone, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var index = await GitAsync(clone, "ls-files", "--stage");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.Equal("Skipped: the working tree has local changes.", service.GetStatus(instance.Id).Message);
        Assert.Null(service.GetStatus(instance.Id).LastSuccess);
        Assert.Equal(head, await GitAsync(clone, "rev-parse", "HEAD"));
        Assert.Equal(tree, await GitAsync(clone, "status", "--porcelain=v1", "-z", "--untracked-files=all"));
        Assert.Equal(index, await GitAsync(clone, "ls-files", "--stage"));
        Assert.Equal("local change", await File.ReadAllTextAsync(Path.Combine(clone, filename)));
        Assert.Equal(string.Empty, await GitAsync(clone, "stash", "list"));
    }

    [Fact]
    public async Task IgnoredUntrackedFilesDoNotPreventSync()
    {
        var (source, clone) = await CreateRemoteAsync();
        await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "scratch.txt\n");
        await GitAsync(source, "add", ".");
        await GitAsync(source, "commit", "-m", "Ignore scratch file");
        await GitAsync(source, "push");
        await GitAsync(clone, "pull", "--ff-only");
        await File.WriteAllTextAsync(Path.Combine(clone, "scratch.txt"), "keep this");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.Equal("Already up to date.", service.GetStatus(instance.Id).Message);
        Assert.Equal("keep this", await File.ReadAllTextAsync(Path.Combine(clone, "scratch.txt")));
    }

    [Fact]
    public async Task DivergedHistoryIsRejectedWithoutMergeRebaseOrStash()
    {
        var (source, clone) = await CreateRemoteAsync();
        foreach (var directory in new[] { source, clone })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "note.md"), directory == source ? "remote" : "local");
            await GitAsync(directory, "add", ".");
            await GitAsync(directory, "commit", "-m", "Divergent note");
        }
        await GitAsync(source, "push");
        await GitAsync(clone, "config", "pull.rebase", "true");
        await GitAsync(clone, "config", "merge.autostash", "true");
        var head = await GitAsync(clone, "rev-parse", "HEAD");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.StartsWith("Sync failed: fast-forward pull unavailable.", service.GetStatus(instance.Id).Message);
        Assert.Null(service.GetStatus(instance.Id).LastSuccess);
        Assert.Equal(head, await GitAsync(clone, "rev-parse", "HEAD"));
        Assert.Equal("local", await File.ReadAllTextAsync(Path.Combine(clone, "note.md")));
        Assert.Equal(string.Empty, await GitAsync(clone, "status", "--porcelain=v1", "-z"));
        Assert.Equal(string.Empty, await GitAsync(clone, "stash", "list"));
        Assert.False(File.Exists(Path.Combine(clone, ".git", "MERGE_HEAD")));
        Assert.False(Directory.Exists(Path.Combine(clone, ".git", "rebase-merge")));
        Assert.False(Directory.Exists(Path.Combine(clone, ".git", "rebase-apply")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonRepositoryAndFolderInsideAncestorRepositoryAreSkipped(bool ancestor)
    {
        if (ancestor) await GitAsync(_root, "init", "--initial-branch=main");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "not a repo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "note.md"), "unchanged");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", folder, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.Equal("Skipped: the Notes folder is not a Git repository.", service.GetStatus(instance.Id).Message);
        Assert.False(service.GetStatus(instance.Id).IsRunning);
        Assert.Null(service.GetStatus(instance.Id).LastSuccess);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(folder, "note.md")));
    }

    [Fact]
    public async Task RepositoryWithoutRemoteReportsCoarseFailure()
    {
        var (_, clone) = await CreateRemoteAsync();
        await GitAsync(clone, "remote", "remove", "origin");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        using var service = CreateService(instance);

        await service.PullAsync(instance.Id);

        Assert.StartsWith("Sync failed: fast-forward pull unavailable.", service.GetStatus(instance.Id).Message);
        Assert.Null(service.GetStatus(instance.Id).LastSuccess);
        Assert.False(service.GetStatus(instance.Id).IsRunning);
    }

    [Fact]
    public async Task OnlyOptedInNotesSyncAtStartup()
    {
        var (_, clone) = await CreateRemoteAsync();
        var enabled = new LocalLibraryInstance(Guid.NewGuid(), "Enabled", clone, LocalLibraryKind.Notes) with { AutoGitPull = true };
        var disabled = enabled with { Id = Guid.NewGuid(), AutoGitPull = false };
        var calibre = enabled with { Id = Guid.NewGuid(), Kind = LocalLibraryKind.CalibreText };
        using var service = CreateService(enabled, disabled, calibre);
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (service.GetStatus(enabled.Id).LastSuccess is null) await Task.Delay(25, timeout.Token);
            Assert.Equal("Already up to date.", service.GetStatus(enabled.Id).Message);
            Assert.Null(service.GetStatus(disabled.Id).LastAttempt);
            Assert.Null(service.GetStatus(calibre.Id).LastAttempt);
            await service.PullAsync(disabled.Id);
            Assert.NotNull(service.GetStatus(disabled.Id).LastSuccess);
            await service.PullAsync(calibre.Id);
            Assert.Equal("Skipped: not a Notes library.", service.GetStatus(calibre.Id).Message);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationStopsOwnedGitProcessAndReleasesOverlapGate()
    {
        var (_, clone) = await CreateRemoteAsync();
        var marker = Path.Combine(_root, "transport-started");
        var markerForGit = marker.Replace('\\', '/');
        await GitAsync(clone, "config", "remote.origin.uploadpack", $"touch '{markerForGit}'; sleep 30; git-upload-pack");
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        var other = instance with { Id = Guid.NewGuid() };
        using var service = CreateService(instance, other);
        using var cancellation = new CancellationTokenSource();
        var pull = service.PullAsync(instance.Id, cancellation.Token);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker)) await Task.Delay(25, timeout.Token);
            var status = service.GetStatus(instance.Id);
            Assert.True(status.IsRunning);
            await service.PullAsync(instance.Id);
            Assert.Equal(status, service.GetStatus(instance.Id));
            await service.PullAsync(other.Id);
            Assert.Equal("Another Notes sync is running.", service.GetStatus(other.Id).Message);
            Assert.Null(service.GetStatus(other.Id).LastAttempt);
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            await pull.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal("Sync canceled.", service.GetStatus(instance.Id).Message);
            Assert.False(service.GetStatus(instance.Id).IsRunning);
            Assert.Null(service.GetStatus(instance.Id).LastSuccess);
            Assert.False(File.Exists(Path.Combine(clone, ".git", "index.lock")));
            await GitAsync(clone, "config", "--unset", "remote.origin.uploadpack");
            await service.PullAsync(instance.Id);
            Assert.Equal("Already up to date.", service.GetStatus(instance.Id).Message);
        }
        finally
        {
            cancellation.Cancel();
            await pull.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PreCanceledAttemptDoesNotRunGitOrChangeStatus()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", _root, LocalLibraryKind.Notes);
        using var service = CreateService(instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PullAsync(instance.Id, cancellation.Token));

        Assert.Equal(new NotesGitSyncStatus("Not synced yet."), service.GetStatus(instance.Id));
    }

    [Fact]
    public async Task RemoteFailureNeverExposesRawGitOutputAndRetainsLastSuccess()
    {
        var (_, clone) = await CreateRemoteAsync();
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", clone, LocalLibraryKind.Notes);
        var logger = new CapturingLogger();
        using var service = new NotesGitSyncService(new MemoryStore([instance]), logger);
        await service.PullAsync(instance.Id);
        var success = service.GetStatus(instance.Id).LastSuccess;
        Assert.NotNull(success);
        var missingRemote = Path.Combine(_root, "private-remote-sentinel.git");
        await GitAsync(clone, "remote", "set-url", "origin", missingRemote);

        await service.PullAsync(instance.Id);

        var status = service.GetStatus(instance.Id);
        Assert.StartsWith("Sync failed: fast-forward pull unavailable.", status.Message);
        Assert.Equal(success, status.LastSuccess);
        Assert.DoesNotContain("private-remote-sentinel", status.Message);
        Assert.DoesNotContain(_root, status.Message);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("private-remote-sentinel", message);
            Assert.DoesNotContain(_root, message);
            Assert.DoesNotContain("fatal:", message);
        });
    }

    private NotesGitSyncService CreateService(params LocalLibraryInstance[] instances) =>
        new(new MemoryStore(instances), NullLogger<NotesGitSyncService>.Instance);

    private async Task<(string Source, string Clone)> CreateRemoteAsync()
    {
        var remote = Directory.CreateDirectory(Path.Combine(_root, "remote.git")).FullName;
        await GitAsync(remote, "init", "--bare", "--initial-branch=main");
        var source = Path.Combine(_root, "source");
        await GitAsync(_root, "clone", remote, source);
        await ConfigureAsync(source);
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), "original note");
        await GitAsync(source, "add", ".");
        await GitAsync(source, "commit", "-m", "Initial note");
        await GitAsync(source, "push", "-u", "origin", "main");
        var clone = Path.Combine(_root, "notes");
        await GitAsync(_root, "clone", remote, clone);
        await ConfigureAsync(clone);
        return (source, clone);
    }

    private static async Task ConfigureAsync(string directory)
    {
        await GitAsync(directory, "config", "user.name", "Notes test");
        await GitAsync(directory, "config", "user.email", "notes@example.invalid");
        await GitAsync(directory, "config", "commit.gpgSign", "false");
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("credential.interactive=Never");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, "Local Git fixture command failed.");
        await error;
        return await output;
    }

    private sealed class CapturingLogger : ILogger<NotesGitSyncService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class MemoryStore(LocalLibraryInstance[] instances) : ILocalLibraryStore
    {
        public Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LocalLibraryInstance>>(instances);
        }

        public Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}