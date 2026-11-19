using System.Collections.Concurrent;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LibrarySearchServiceTests
{
    [Fact]
    public async Task SearchAsync_SearchesEveryKindAndSourceAndClampsEachGroup()
    {
        StashInstance[] stashInstances = [StashInstance("First"), StashInstance("Second")];
        HydrusInstance[] hydrusInstances = [HydrusInstance("Third"), HydrusInstance("Fourth")];
        var stashCalls = new ConcurrentBag<(Guid Id, StashSearchKind Kind, string Query)>();
        var hydrusCalls = new ConcurrentBag<Guid>();
        var stash = new FakeStashClient((instance, kind, query, _) =>
        {
            stashCalls.Add((instance.Id, kind, query));
            return Task.FromResult<IReadOnlyList<StashSearchResult>>(Enumerable.Range(1, 8)
                .Select(index => StashResult(instance, kind, index)).ToArray());
        });
        var hydrus = new FakeHydrusClient((instance, tags, _) =>
        {
            hydrusCalls.Add(instance.Id);
            Assert.Equal(new[] { "creator:some person", "tag" }, tags);
            return Task.FromResult<IReadOnlyList<HydrusFile>>(Enumerable.Range(1, 9)
                .Select(index => new HydrusFile(6000000000L + index, $"File {index}", "image/png")).ToArray());
        });

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync(stashInstances, hydrusInstances, "  creator:some person, tag  ");

        Assert.Equal(8, groups.Count);
        Assert.Equal(6, stashCalls.Count);
        Assert.Equal(hydrusInstances.Select(instance => instance.Id).Order(), hydrusCalls.Order());
        foreach (var instance in stashInstances)
        {
            foreach (var kind in Enum.GetValues<StashSearchKind>())
            {
                Assert.Single(stashCalls, call => call == (instance.Id, kind, "creator:some person, tag"));
                var group = Assert.Single(groups, group => group.InstanceId == instance.Id && group.Category == kind.ToString());
                Assert.Equal(instance.Name, group.InstanceName);
                Assert.Equal("Stash", group.Provider);
                Assert.Equal(new LibraryItem("1", "Result 1", kind.ToString(), new Uri("https://stash.example/items/1"),
                    new Uri($"/previews/stash/{instance.Id}/{kind.ToString().ToLowerInvariant()}/1", UriKind.Relative)), group.Items[0]);
                Assert.Equal(new[] { "1", "2", "3", "4", "5" }, group.Items.Select(item => item.Id));
            }
        }

        foreach (var instance in hydrusInstances)
        {
            var group = Assert.Single(groups, group => group.InstanceId == instance.Id);
            Assert.Equal(instance.Name, group.InstanceName);
            Assert.Equal("Hydrus", group.Provider);
            Assert.Equal("Files", group.Category);
            Assert.Null(group.SearchUrl);
            Assert.Equal(new LibraryItem("6000000001", "File 1", "image/png", null,
                new Uri($"/previews/hydrus/{instance.Id}/files/6000000001", UriKind.Relative)), group.Items[0]);
            Assert.Equal(new[] { "6000000001", "6000000002", "6000000003", "6000000004", "6000000005" }, group.Items.Select(item => item.Id));
        }

        Assert.All(groups, group =>
        {
            Assert.Equal(5, group.Items.Count);
            Assert.Null(group.Error);
        });
    }

    [Fact]
    public async Task SearchAsync_UsesLocalPreviewLinksEvenWhenStashReturnsAnUntrustedThumbnail()
    {
        var instance = StashInstance("Stash") with { ApiKey = "private-key" };
        var stash = new FakeStashClient((source, kind, _, _) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            [StashResult(source, kind) with { ThumbnailUrl = new Uri("https://evil.example/thumbnail?apikey=private-key") }]));

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient()).SearchAsync([instance], [], "query");

        Assert.All(groups, group =>
        {
            var link = Assert.Single(group.Items).PreviewUrl!.OriginalString;
            Assert.Equal($"/previews/stash/{instance.Id}/{group.Category.ToLowerInvariant()}/1", link);
            Assert.DoesNotContain("private-key", link);
            Assert.DoesNotContain("evil.example", link);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_IsolatesFailuresWithoutExposingProviderErrors(bool providerCancellation)
    {
        var failingStash = StashInstance("Failing category");
        var healthyStash = StashInstance("Healthy instance");
        var failingHydrus = HydrusInstance("Failing files");
        var healthyHydrus = HydrusInstance("Healthy files");
        Exception failure = providerCancellation
            ? new OperationCanceledException("private-key provider timeout")
            : new HttpRequestException("private-key provider response");
        var stash = new FakeStashClient((instance, kind, _, _) =>
            instance.Id == failingStash.Id && kind == StashSearchKind.Images
                ? Task.FromException<IReadOnlyList<StashSearchResult>>(failure)
                : Task.FromResult<IReadOnlyList<StashSearchResult>>([StashResult(instance, kind)]));
        var hydrus = new FakeHydrusClient((instance, _, _) => instance.Id == failingHydrus.Id
            ? Task.FromException<IReadOnlyList<HydrusFile>>(failure)
            : Task.FromResult<IReadOnlyList<HydrusFile>>([new HydrusFile(1, "File", "image/png")]));

        var groups = await new LibrarySearchService(stash, hydrus)
            .SearchAsync([failingStash, healthyStash], [failingHydrus, healthyHydrus], "query");

        Assert.Equal(8, groups.Count);
        Assert.Equal(6, groups.Count(group => group.Error is null));
        var failedGroups = groups.Where(group => group.Error is not null).ToArray();
        Assert.Equal(2, failedGroups.Length);
        Assert.Contains(failedGroups, group => group.InstanceId == failingStash.Id && group.Category == "Images" && group.SearchUrl is not null);
        Assert.Contains(failedGroups, group => group.InstanceId == failingHydrus.Id && group.Category == "Files" && group.SearchUrl is null);
        Assert.All(failedGroups, group =>
        {
            Assert.Empty(group.Items);
            Assert.Equal("Search failed. Check the instance connection and try again.", group.Error);
        });
        Assert.All(groups.Where(group => group.Error is null), group => Assert.Single(group.Items));
    }

    [Fact]
    public async Task SearchAsync_EncodesTrimmedQueryForEveryStashCategory()
    {
        var instance = StashInstance("Nested UI") with { WebUiUrl = new Uri("https://stash.example/library/") };
        const string query = "  name & value/#?+=, non-ascii \u00e9  ";
        var seenQueries = new ConcurrentBag<string>();
        var stash = new FakeStashClient((_, _, capturedQuery, _) =>
        {
            seenQueries.Add(capturedQuery);
            return Task.FromResult<IReadOnlyList<StashSearchResult>>([]);
        });

        var groups = await new LibrarySearchService(stash, new FakeHydrusClient()).SearchAsync([instance], [], query);

        Assert.Equal(3, seenQueries.Count);
        Assert.All(seenQueries, capturedQuery => Assert.Equal(query.Trim(), capturedQuery));
        foreach (var group in groups)
        {
            Assert.Equal($"https://stash.example/library/{group.Category.ToLowerInvariant()}?q=name%20%26%20value%2F%23%3F%2B%3D%2C%20non-ascii%20%C3%A9",
                group.SearchUrl!.AbsoluteUri);
            Assert.Empty(group.Items);
            Assert.Null(group.Error);
        }
    }

    [Theory]
    [InlineData(" creator:some person, , tag with spaces ,, -excluded ", new[] { "creator:some person", "tag with spaces", "-excluded" })]
    [InlineData("one tag", new[] { "one tag" })]
    [InlineData(" , , \t ", new string[0])]
    [InlineData("", new string[0])]
    public async Task ParseHydrusTags_UsesTheSameCommaSeparatedTagsAsSearch(string query, string[] expected)
    {
        Assert.Equal(expected, LibrarySearchService.ParseHydrusTags(query));
        var hydrus = new FakeHydrusClient((_, tags, _) =>
        {
            Assert.Equal(expected, tags);
            return Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        });

        var groups = await new LibrarySearchService(new FakeStashClient(), hydrus)
            .SearchAsync([], [HydrusInstance("Files")], query);

        var group = Assert.Single(groups);
        Assert.Null(group.Error);
        Assert.Null(group.SearchUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_PreservesPreCanceledCallerEvenWithoutInstances(bool includeInstances)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient());

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(
            includeInstances ? [StashInstance("Stash")] : [],
            includeInstances ? [HydrusInstance("Hydrus")] : [], "query", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task SearchAsync_CancelsActiveAndQueuedWork()
    {
        using var cancellation = new CancellationTokenSource();
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task WaitForCancellation(CancellationToken token)
        {
            Assert.Equal(cancellation.Token, token);
            if (Interlocked.Increment(ref calls) == 6)
            {
                sixStarted.SetResult();
            }
            await Task.Delay(Timeout.Infinite, token);
        }
        var stash = new FakeStashClient(async (_, _, _, token) =>
        {
            await WaitForCancellation(token);
            return [];
        });
        var hydrus = new FakeHydrusClient(async (_, _, token) =>
        {
            await WaitForCancellation(token);
            return [];
        });
        var search = new LibrarySearchService(stash, hydrus).SearchAsync(
            [StashInstance("First"), StashInstance("Second"), StashInstance("Queued")],
            [HydrusInstance("Queued files")], "query", cancellation.Token);

        try
        {
            await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(6, calls);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task SearchAsync_LimitsConcurrencyAcrossProvidersAndCapturesQueryPerCall()
    {
        var sixStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var calls = 0;
        async Task Block()
        {
            var current = Interlocked.Increment(ref active);
            Assert.InRange(current, 1, 6);
            if (Interlocked.Increment(ref calls) == 6)
            {
                sixStarted.TrySetResult();
            }
            await release.Task;
            Interlocked.Decrement(ref active);
        }
        var stash = new FakeStashClient(async (_, _, query, _) =>
        {
            Assert.Equal("original tag, second", query);
            await Block();
            return [];
        });
        var hydrus = new FakeHydrusClient(async (_, tags, _) =>
        {
            Assert.Equal(new[] { "original tag", "second" }, tags);
            await Block();
            return [];
        });
        var query = "  original tag, second  ";
        var search = new LibrarySearchService(stash, hydrus).SearchAsync(
            [StashInstance("Stash")], Enumerable.Range(1, 7).Select(index => HydrusInstance($"Hydrus {index}")).ToArray(), query);
        query = "edited query";

        try
        {
            await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(6, Volatile.Read(ref calls));
            Assert.False(search.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var groups = await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, calls);
        Assert.Equal(10, groups.Count);
        Assert.All(groups, group => Assert.Null(group.Error));
    }

    private static StashInstance StashInstance(string name) =>
        new(Guid.NewGuid(), name, new Uri("https://stash.example/graphql"), new Uri("https://stash.example/"), null);

    private static HydrusInstance HydrusInstance(string name) =>
        new(Guid.NewGuid(), name, new Uri("https://hydrus.example/"), null);

    private static StashSearchResult StashResult(StashInstance instance, StashSearchKind kind, int index = 1) =>
        new(index.ToString(), $"Result {index}", kind, null, new Uri($"https://stash.example/items/{index}"), instance.Id, instance.Name);

    private sealed class FakeStashClient(
        Func<StashInstance, StashSearchKind, string, CancellationToken, Task<IReadOnlyList<StashSearchResult>>>? search = null) : IStashGraphQlClient
    {
        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query, CancellationToken cancellationToken = default) =>
            search?.Invoke(instance, kind, query, cancellationToken) ?? Task.FromResult<IReadOnlyList<StashSearchResult>>([]);

        public Task<StashObservation> ObserveAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHydrusClient(
        Func<HydrusInstance, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HydrusFile>>>? search = null) : IHydrusClient
    {
        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            search?.Invoke(instance, tags, cancellationToken) ?? Task.FromResult<IReadOnlyList<HydrusFile>>([]);

        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}