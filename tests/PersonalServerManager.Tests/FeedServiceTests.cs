using PersonalServerManager.Feeds;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class FeedServiceTests
{
    private readonly StashInstance stash = new(Guid.NewGuid(), "Stash library", new Uri("https://stash.test/graphql"), new Uri("https://stash.test/"), "secret");
    private readonly HydrusInstance hydrus = new(Guid.NewGuid(), "Hydrus library", new Uri("https://hydrus.test/"), "secret");
    private readonly SwarmUIInstance swarm = new(Guid.NewGuid(), "Swarm library", new Uri("https://swarm.test/base/"), SwarmUIInstance.DefaultSearchPaths);

    [Fact]
    public async Task MixesSourcesAndStashStreamsAcrossPagesUntilExhausted()
    {
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            Enumerable.Range(1, 25).Skip(offset).Take(limit).Select(index => StashFile(instance, kind, index)).ToArray());
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            Enumerable.Range(1, 25).Skip(offset).Take(limit).Select(index => new HydrusFile(index, $"h{index}", "image/png")).ToArray());
        var session = Service(clients).Start(Feed(new(stash.Id, FeedProvider.Stash), new(hydrus.Id, FeedProvider.Hydrus)));
        var all = new List<FeedItem>();
        FeedPage page;
        do
        {
            page = await session.LoadNextAsync();
            Assert.Empty(page.Errors);
            all.AddRange(page.Items);
        } while (page.HasMore);

        Assert.Equal(75, all.Count);
        Assert.Equal(75, all.Select(item => item.Id).Distinct().Count());
        Assert.Equal(new[] { "Stash", "Hydrus", "Stash", "Hydrus", "Stash", "Hydrus" }, all.Take(6).Select(item => item.Provider));
        Assert.Equal(new[] { true, false, true, false }, all.Where(item => item.Provider == "Stash").Take(4).Select(item => item.IsVideo));
        Assert.Contains(clients.Calls, call => call == "Stash:Scenes:20:20");
        Assert.Contains(clients.Calls, call => call == "Stash:Images:20:20");
        Assert.Contains(clients.Calls, call => call == "Hydrus:20:20");
        var scene = all.First(item => item.Provider == "Stash");
        Assert.Equal($"/media/stash/{stash.Id:D}/scenes/1", scene.MediaUrl.OriginalString);
        Assert.Equal("https://stash.test/scenes/1", scene.SourceUrl.AbsoluteUri);
        Assert.False((await session.LoadNextAsync()).HasMore);
    }

    [Fact]
    public async Task FiltersUnsupportedAndDuplicateItemsAndMapsSwarmLinks()
    {
        var clients = new Clients();
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            offset == 0 ? [new(1, "text", "text/plain"), new(2, "unsupported", "image/tiff"), new(3, "video", "video/mp4; codecs=x"), new(3, "duplicate", "video/mp4")] : []);
        clients.Swarm = (instance, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<SwarmUIFile>>(
            offset == 0 ? [new("raw/a b.png", SwarmUIMediaKind.Image), new("raw/b.txt", SwarmUIMediaKind.Image), new("raw/c.png", SwarmUIMediaKind.Video)] : []);
        var page = await Service(clients).Start(Feed(new(hydrus.Id, FeedProvider.Hydrus), new(swarm.Id, FeedProvider.SwarmUI))).LoadNextAsync();

        Assert.Equal(2, page.Items.Count);
        var video = Assert.Single(page.Items, item => item.Provider == "Hydrus");
        Assert.True(video.IsVideo);
        Assert.Equal(video.MediaUrl, video.SourceUrl);
        var image = Assert.Single(page.Items, item => item.Provider == "SwarmUI");
        Assert.Equal($"/media/swarmui/{swarm.Id:D}/file?path=raw%2Fa%20b.png", image.MediaUrl.OriginalString);
        Assert.Equal("https://swarm.test/base/View/local/raw/a%20b.png", image.SourceUrl.AbsoluteUri);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task FullFilteredBatchAdvancesAndShortFinalBatchExhausts()
    {
        var clients = new Clients();
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            offset == 0 ? Enumerable.Range(1, 20).Select(index => new HydrusFile(index, "unsupported", "application/pdf")).ToArray() :
            [new(21, "visible", "image/jpeg")]);
        var page = await Service(clients).Start(Feed(new FeedSource(hydrus.Id, FeedProvider.Hydrus))).LoadNextAsync();
        Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Contains("Hydrus:20:20", clients.Calls);
    }

    [Fact]
    public async Task DeduplicatesAcrossBatchesAndStillLoadsLaterItems()
    {
        var clients = new Clients();
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            offset == 0 ? Enumerable.Range(1, 20).Select(index => new HydrusFile(index, $"h{index}", "image/png")).ToArray() :
            [new(1, "duplicate", "image/png"), new(21, "later", "image/png")]);
        var session = Service(clients).Start(Feed(new FeedSource(hydrus.Id, FeedProvider.Hydrus)));
        var first = await session.LoadNextAsync();
        var second = await session.LoadNextAsync();
        Assert.Equal(20, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal("later", Assert.Single(second.Items).Title);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task IsolatesErrorsAndMissingInstancesWithoutLeakingRemoteMessages()
    {
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => kind == StashSearchKind.Scenes
            ? throw new HttpRequestException("secret remote response")
            : Task.FromResult<IReadOnlyList<StashSearchResult>>([StashFile(instance, kind, 1)]);
        var missing = Guid.NewGuid();
        var page = await Service(clients).Start(Feed(new(stash.Id, FeedProvider.Stash), new(missing, FeedProvider.Hydrus))).LoadNextAsync();
        Assert.Single(page.Items);
        Assert.Equal(2, page.Errors.Count);
        Assert.Contains(page.Errors, error => error.Contains("missing", StringComparison.OrdinalIgnoreCase) && error.Contains(missing.ToString()));
        Assert.DoesNotContain(page.Errors, error => error.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task CancellationPreservesItemsAlreadyConsumedForRetry()
    {
        var clients = new Clients();
        using var cancellation = new CancellationTokenSource();
        var cancel = true;
        clients.Hydrus = (instance, query, media, order, offset, limit, token) =>
        {
            if (cancel) { cancel = false; cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult<IReadOnlyList<HydrusFile>>([new(1, "hydrus", "image/png")]);
        };
        clients.Stash = (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            kind == StashSearchKind.Scenes ? [StashFile(instance, kind, 1)] : []);
        var session = Service(clients).Start(Feed(new(stash.Id, FeedProvider.Stash) { Media = FeedMedia.Videos }, new(hydrus.Id, FeedProvider.Hydrus)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LoadNextAsync(cancellation.Token));
        var page = await session.LoadNextAsync();
        Assert.Equal(2, page.Items.Count);
        Assert.Empty(page.Errors);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task PassesSourceFiltersOrderAndMediaToProviders()
    {
        var clients = new Clients();
        var stashSource = new FeedSource(stash.Id, FeedProvider.Stash) { Media = FeedMedia.Images, IncludeTags = ["tag"] };
        var hydrusSource = new FeedSource(hydrus.Id, FeedProvider.Hydrus) { Media = FeedMedia.Videos, HydrusQuery = "series:test" };
        var swarmSource = new FeedSource(swarm.Id, FeedProvider.SwarmUI) { SwarmFolder = "raw/folder", SwarmQuery = "prompt" };
        clients.Stash = (instance, kind, source, order, offset, limit, token) =>
        {
            Assert.Equal(StashSearchKind.Images, kind);
            Assert.Equal(stashSource, source);
            Assert.Equal(FeedOrder.Random, order);
            return Task.FromResult<IReadOnlyList<StashSearchResult>>([]);
        };
        clients.Hydrus = (instance, query, media, order, offset, limit, token) =>
        {
            Assert.Equal(("series:test", FeedMedia.Videos, FeedOrder.Random), (query, media, order));
            return Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        };
        clients.Swarm = (instance, source, order, offset, limit, token) =>
        {
            Assert.Equal(swarmSource, source);
            Assert.Equal(FeedOrder.Random, order);
            return Task.FromResult<IReadOnlyList<SwarmUIFile>>([]);
        };
        var page = await Service(clients).Start(new FeedDefinition(Guid.NewGuid(), "Feed", FeedOrder.Random,
            [stashSource, hydrusSource, swarmSource])).LoadNextAsync();
        Assert.Empty(page.Errors);
        Assert.False(page.HasMore);
        Assert.Single(clients.Calls, call => call.StartsWith("Stash:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationReportedAsOtherFailureDoesNotDisableStream()
    {
        var clients = new Clients();
        using var cancellation = new CancellationTokenSource();
        var first = true;
        clients.Hydrus = (instance, query, media, order, offset, limit, token) =>
        {
            if (first) { first = false; cancellation.Cancel(); throw new HttpRequestException("interrupted"); }
            return Task.FromResult<IReadOnlyList<HydrusFile>>([new(1, "visible", "image/png")]);
        };
        var session = Service(clients).Start(Feed(new FeedSource(hydrus.Id, FeedProvider.Hydrus)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LoadNextAsync(cancellation.Token));
        var page = await session.LoadNextAsync();
        Assert.Single(page.Items);
        Assert.Empty(page.Errors);
    }

    private FeedService Service(Clients clients) => new(new StashStore([stash]), clients, new HydrusStore([hydrus]), clients, new SwarmStore([swarm]), clients);
    private static FeedDefinition Feed(params FeedSource[] sources) => new(Guid.NewGuid(), "Feed", FeedOrder.Newest, sources);
    private static StashSearchResult StashFile(StashInstance instance, StashSearchKind kind, int index) =>
        new(index.ToString(), $"{kind}{index}", kind, null, new Uri($"https://stash.test/{kind.ToString().ToLowerInvariant()}/{index}"), instance.Id, instance.Name);

    private sealed class StashStore(IReadOnlyList<StashInstance> instances) : IStashInstanceStore
    {
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(instances);
        public Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class HydrusStore(IReadOnlyList<HydrusInstance> instances) : IHydrusInstanceStore
    {
        public Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(instances);
        public Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class SwarmStore(IReadOnlyList<SwarmUIInstance> instances) : ISwarmUIInstanceStore
    {
        public Task<IReadOnlyList<SwarmUIInstance>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(instances);
        public Task SaveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Clients : IStashGraphQlClient, IHydrusClient, ISwarmUIClient
    {
        public List<string> Calls { get; } = [];
        public Func<StashInstance, StashSearchKind, FeedSource, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<StashSearchResult>>> Stash { get; set; } =
            (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>([]);
        public Func<HydrusInstance, string, FeedMedia, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<HydrusFile>>> Hydrus { get; set; } =
            (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        public Func<SwarmUIInstance, FeedSource, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<SwarmUIFile>>> Swarm { get; set; } =
            (instance, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<SwarmUIFile>>([]);

        public Task<IReadOnlyList<StashSearchResult>> SearchFeedAsync(StashInstance instance, StashSearchKind kind, FeedSource source, FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default)
        { Calls.Add($"Stash:{kind}:{offset}:{limit}"); return Stash(instance, kind, source, order, offset, limit, cancellationToken); }
        public Task<IReadOnlyList<HydrusFile>> SearchFeedAsync(HydrusInstance instance, string query, FeedMedia media, FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default)
        { Calls.Add($"Hydrus:{offset}:{limit}"); return Hydrus(instance, query, media, order, offset, limit, cancellationToken); }
        public Task<IReadOnlyList<SwarmUIFile>> SearchFeedAsync(SwarmUIInstance instance, FeedSource source, FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default) => Swarm(instance, source, order, offset, limit, cancellationToken);
        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StashObservation> ObserveAsync(StashInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SwarmUIObservation> ObserveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SwarmUIFile>> SearchAsync(SwarmUIInstance instance, string query, int limit, LibrarySort sort, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}