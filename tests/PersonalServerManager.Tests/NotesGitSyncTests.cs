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
            Assert.Equal("Another Notes sync or removal is running.", service.GetStatus(other.Id).Message);
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
    public async Task RemovalIsRejectedWhileSyncOwnsGateAndKeepsFiles()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", _root, LocalLibraryKind.Notes);
        var other = instance with { Id = Guid.NewGuid(), Name = "Other Notes" };
        var note = Path.Combine(_root, "note.md");
        await File.WriteAllTextAsync(note, "keep this");
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(instance);
        await store.SaveAsync(other);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterGet = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        var pull = service.PullAsync(instance.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveAsync(instance.Id));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveAsync(other.Id));
            Assert.True(service.GetStatus(instance.Id).IsRunning);
        }
        finally
        {
            release.TrySetResult();
            await pull.WaitAsync(TimeSpan.FromSeconds(10));
        }

        store.AfterGet = null;
        Assert.Equal(2, (await store.GetAllAsync()).Count);
        await service.RemoveAsync(instance.Id);
        Assert.Equal(other.Id, Assert.Single(await store.GetAllAsync()).Id);
        Assert.Equal("keep this", await File.ReadAllTextAsync(note));
    }

    [Fact]
    public async Task SyncCannotCrossRemovalAndRevalidatesAfterward()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", _root, LocalLibraryKind.Notes);
        var note = Path.Combine(_root, "note.md");
        await File.WriteAllTextAsync(note, "keep this");
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRemove = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        var removal = service.RemoveAsync(instance.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.PullAsync(instance.Id);
            Assert.Null(service.GetStatus(instance.Id).LastAttempt);
            Assert.Equal("Another Notes sync or removal is running.", service.GetStatus(instance.Id).Message);
        }
        finally
        {
            release.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await service.PullAsync(instance.Id);
        Assert.Equal("Skipped: not a Notes library.", service.GetStatus(instance.Id).Message);
        Assert.Empty(await store.GetAllAsync());
        Assert.Equal("keep this", await File.ReadAllTextAsync(note));
    }

    [Fact]
    public async Task ScheduledSyncWaitsForRemovalAndAttemptsUnrelatedLibrary()
    {
        var removed = new LocalLibraryInstance(Guid.NewGuid(), "Removed", _root, LocalLibraryKind.Notes) { AutoGitPull = true };
        var scheduled = removed with { Id = Guid.NewGuid(), Name = "Scheduled", AutoGitPull = true };
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(removed);
        await store.SaveAsync(scheduled);
        var removalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRemove = async token => { removalEntered.TrySetResult(); await releaseRemoval.Task.WaitAsync(token); };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        var removal = service.RemoveAsync(removed.Id);
        try
        {
            await removalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.AfterGet = _ => { snapshotRead.TrySetResult(); return Task.CompletedTask; };
            await service.StartAsync(CancellationToken.None);
            await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            Assert.Null(service.GetStatus(scheduled.Id).LastAttempt);
            releaseRemoval.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (service.GetStatus(scheduled.Id) is { LastAttempt: null } or { IsRunning: true })
                await Task.Delay(25, timeout.Token);
            Assert.Equal("Skipped: not a Notes library.", service.GetStatus(removed.Id).Message);
            Assert.Equal("Skipped: the Notes folder is not a Git repository.", service.GetStatus(scheduled.Id).Message);
        }
        finally
        {
            releaseRemoval.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ScheduledSyncWaitsForManualSyncWithoutChangingManualDeduplication()
    {
        var manual = new LocalLibraryInstance(Guid.NewGuid(), "Manual", _root, LocalLibraryKind.Notes);
        var scheduled = manual with { Id = Guid.NewGuid(), Name = "Scheduled", AutoGitPull = true };
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(manual);
        await store.SaveAsync(scheduled);
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        store.AfterGet = async token =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                manualEntered.TrySetResult();
                await releaseManual.Task.WaitAsync(token);
            }
            else snapshotRead.TrySetResult();
        };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        var pull = service.PullAsync(manual.Id);
        try
        {
            await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var originalStatus = service.GetStatus(manual.Id);
            await service.PullAsync(manual.Id);
            Assert.Equal(originalStatus, service.GetStatus(manual.Id));
            await service.PullAsync(scheduled.Id);
            Assert.Equal("Another Notes sync or removal is running.", service.GetStatus(scheduled.Id).Message);
            Assert.Null(service.GetStatus(scheduled.Id).LastAttempt);
            await service.StartAsync(CancellationToken.None);
            await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            Assert.Null(service.GetStatus(scheduled.Id).LastAttempt);
            releaseManual.TrySetResult();
            await pull.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (service.GetStatus(scheduled.Id) is { LastAttempt: null } or { IsRunning: true })
                await Task.Delay(25, timeout.Token);
            Assert.Equal("Skipped: the Notes folder is not a Git repository.", service.GetStatus(scheduled.Id).Message);
        }
        finally
        {
            releaseManual.TrySetResult();
            await pull.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ShutdownCancelsScheduledSyncWaitingForRemoval()
    {
        var removed = new LocalLibraryInstance(Guid.NewGuid(), "Removed", _root, LocalLibraryKind.Notes);
        var scheduled = removed with { Id = Guid.NewGuid(), Name = "Scheduled", AutoGitPull = true };
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(removed);
        await store.SaveAsync(scheduled);
        var removalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRemove = async token => { removalEntered.TrySetResult(); await releaseRemoval.Task.WaitAsync(token); };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        var removal = service.RemoveAsync(removed.Id);
        try
        {
            await removalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.AfterGet = _ => { snapshotRead.TrySetResult(); return Task.CompletedTask; };
            await service.StartAsync(CancellationToken.None);
            await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            var stop = service.StopAsync(CancellationToken.None);
            releaseRemoval.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(service.GetStatus(scheduled.Id).LastAttempt);
        }
        finally
        {
            releaseRemoval.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ScheduledSnapshotCannotSyncAfterRemoval()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Notes", _root, LocalLibraryKind.Notes) { AutoGitPull = true };
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        store.AfterGet = async token =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.RemoveAsync(instance.Id);
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (service.GetStatus(instance.Id) is { LastAttempt: null } or { IsRunning: true })
                await Task.Delay(25, timeout.Token);
            Assert.Equal("Skipped: not a Notes library.", service.GetStatus(instance.Id).Message);
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RemovalRevalidatesTheLibraryKindAndReleasesGateOnFailure()
    {
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Calibre", _root, LocalLibraryKind.Calibre);
        var store = new BlockingStore(new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json")));
        await store.SaveAsync(instance);
        using var service = new NotesGitSyncService(store, NullLogger<NotesGitSyncService>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveAsync(instance.Id));
        Assert.Single(await store.GetAllAsync());
        await service.PullAsync(instance.Id);
        Assert.Equal("Skipped: not a Notes library.", service.GetStatus(instance.Id).Message);
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

    private sealed class BlockingStore(ILocalLibraryStore inner) : ILocalLibraryStore
    {
        public Func<CancellationToken, Task>? AfterGet { get; set; }
        public Func<CancellationToken, Task>? BeforeRemove { get; set; }

        public async Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var instances = await inner.GetAllAsync(cancellationToken);
            if (AfterGet is not null) await AfterGet(cancellationToken);
            return instances;
        }

        public Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(instance, cancellationToken);

        public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (BeforeRemove is not null) await BeforeRemove(cancellationToken);
            await inner.RemoveAsync(id, cancellationToken);
        }
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