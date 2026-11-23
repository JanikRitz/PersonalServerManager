using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class StashJobStateTests
{
    [Fact]
    public async Task StartingIsDeduplicatedAndTrackedByInstance()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        var completion = new TaskCompletionSource<string>();
        var first = state.StartAsync(instanceId, "Scan", () => completion.Task);
        var duplicateCalled = false;

        Assert.True(state.IsStarting(instanceId));
        await state.StartAsync(instanceId, "Generate", () => { duplicateCalled = true; return Task.FromResult("duplicate"); });
        completion.SetResult("job-1");
        await first;

        Assert.False(duplicateCalled);
        Assert.False(state.IsStarting(instanceId));
        Assert.Equal("job-1", Assert.Single(state.Jobs(instanceId)).Id);
        Assert.Empty(state.Jobs(Guid.NewGuid()));
    }

    [Fact]
    public async Task FailedStartClearsBusyState()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.StartAsync(instanceId, "Scan", () => throw new InvalidOperationException()));
        Assert.False(state.IsStarting(instanceId));
        Assert.Empty(state.Jobs(instanceId));
    }

    [Fact]
    public async Task ConcurrentStartsForOneInstanceAreDeduplicated()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var starts = 0;
        var arrived = 0;
        var attempts = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            release.Wait();
            var attempt = state.StartAsync(instanceId, "Scan", () =>
            {
                Interlocked.Increment(ref starts);
                return completion.Task;
            });
            if (Interlocked.Increment(ref arrived) == 64) allArrived.SetResult();
            await attempt;
        })).ToArray();

        release.Set();
        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, starts);
        Assert.True(state.IsStarting(instanceId));
        completion.SetResult("job-1");
        await Task.WhenAll(attempts);
        Assert.False(state.IsStarting(instanceId));
        Assert.Equal("job-1", Assert.Single(state.Jobs(instanceId)).Id);
    }

    [Fact]
    public async Task JobsReturnsStableSnapshotWhileOtherInstancesStartConcurrently()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        await state.StartAsync(instanceId, "Scan", () => Task.FromResult("first"));
        var snapshot = state.Jobs(instanceId);
        var otherInstances = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(otherInstances.Select(id => Task.Run(async () =>
        {
            await state.StartAsync(id, "Scan", () => Task.FromResult(id.ToString()));
            Assert.Single(state.Jobs(id));
            Assert.Equal("first", Assert.Single(snapshot).Id);
        })).Append(Task.Run(() => state.StartAsync(instanceId, "Generate", () => Task.FromResult("second")))));

        Assert.Equal("first", Assert.Single(snapshot).Id);
        Assert.Equal(new[] { "second", "first" }, state.Jobs(instanceId).Select(job => job.Id));
    }

    [Fact]
    public async Task SubscriberFailureDoesNotInterruptStartsOrOtherSubscribers()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        var notifications = 0;
        Action failing = () => throw new ObjectDisposedException("subscriber");
        Action observing = () => { state.Jobs(instanceId); Interlocked.Increment(ref notifications); };
        state.Changed += failing;
        state.Changed += observing;

        await state.StartAsync(instanceId, "Scan", () => Task.FromResult("job-1"));
        state.Changed -= failing;
        state.Changed -= observing;
        await state.StartAsync(instanceId, "Scan", () => Task.FromResult("job-2"));

        Assert.Equal(2, notifications);
        Assert.False(state.IsStarting(instanceId));
        Assert.Equal(2, state.Jobs(instanceId).Count);
    }

    [Fact]
    public async Task ConcurrentReadersAndWritersCanAccessJobsAndSubscriptions()
    {
        var state = new StashJobState();
        var instanceId = Guid.NewGuid();
        using var release = new ManualResetEventSlim();
        Action observer = () => { state.Jobs(instanceId); state.IsStarting(instanceId); };
        var writer = Task.Run(async () =>
        {
            release.Wait();
            for (var index = 0; index < 200; index++)
                await state.StartAsync(instanceId, "Scan", () => Task.FromResult(index.ToString()));
        });
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            release.Wait();
            for (var iteration = 0; iteration < 200; iteration++)
            {
                state.Changed += observer;
                foreach (var job in state.Jobs(instanceId))
                {
                    job.Status = "RUNNING";
                    job.Error = null;
                    job.StatusUnavailable = false;
                    Assert.False(job.IsTerminal);
                }
                state.Changed -= observer;
            }
        })).ToArray();

        release.Set();
        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(200, state.Jobs(instanceId).Count);
        Assert.False(state.IsStarting(instanceId));
    }

    [Theory]
    [InlineData("FINISHED", false, true)]
    [InlineData("FAILED", false, true)]
    [InlineData("CANCELLED", false, true)]
    [InlineData("RUNNING", false, false)]
    [InlineData("FINISHED", true, false)]
    [InlineData(null, true, false)]
    public void TerminalRequiresAnAvailableTerminalStatus(string? status, bool unavailable, bool terminal)
    {
        var job = new TrackedStashJob("1", "Scan") { Status = status, StatusUnavailable = unavailable };
        Assert.Equal(terminal, job.IsTerminal);
    }
}