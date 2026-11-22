using System.Collections.Concurrent;
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
    public async Task StartsSlowSourcesTogetherAndKeepsRoundRobinOrder()
    {
        var clients = new Clients();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clients.Stash = async (instance, kind, source, order, offset, limit, token) =>
        {
            await release.Task.WaitAsync(token);
            return kind == StashSearchKind.Scenes ? [StashFile(instance, kind, 1)] : [];
        };
        clients.Hydrus = async (instance, query, media, order, offset, limit, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return [new HydrusFile(1, "hydrus", "image/png")];
        };
        var session = Service(clients).Start(Feed(new(stash.Id, FeedProvider.Stash), new(hydrus.Id, FeedProvider.Hydrus)));
        var loading = session.LoadNextAsync();
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); }
        var page = await loading;
        Assert.Equal(new[] { "Stash", "Hydrus" }, page.Items.Select(item => item.Provider));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task ContinuesRoundRobinAcrossPagesWithMoreSourcesThanPageSize()
    {
        var clients = new Clients();
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>(
            [new HydrusFile(int.Parse(query), query, "image/png")]);
        var sources = Enumerable.Range(1, 25).Select(index => new FeedSource(hydrus.Id, FeedProvider.Hydrus)
            { HydrusQuery = index.ToString() }).ToArray();
        var session = Service(clients).Start(Feed(sources));
        var first = await session.LoadNextAsync();
        var second = await session.LoadNextAsync();
        Assert.Equal(Enumerable.Range(1, 20).Select(index => index.ToString()), first.Items.Select(item => item.Title));
        Assert.Equal(Enumerable.Range(21, 5).Select(index => index.ToString()), second.Items.Select(item => item.Title));
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Empty(first.Errors);
        Assert.Empty(second.Errors);
    }

    [Theory]
    [InlineData(FeedMedia.All, new[] { "Scenes1", "Images1", "Images2" })]
    [InlineData(FeedMedia.Images, new[] { "Images1" })]
    [InlineData(FeedMedia.Videos, new[] { "Scenes1", "Images2" })]
    public async Task StashFiltersActualMediaAndClassifiesVideoImages(FeedMedia media, string[] titles)
    {
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) =>
        {
            if (kind == StashSearchKind.Images && media == FeedMedia.Videos) Assert.Equal(FeedMedia.All, source.Media);
            return Task.FromResult<IReadOnlyList<StashSearchResult>>(kind == StashSearchKind.Scenes
                ? [StashFile(instance, kind, 1) with { IsVideo = false }]
                : [StashFile(instance, kind, 1), StashFile(instance, kind, 2) with { IsVideo = true }]);
        };
        var page = await Service(clients).Start(Feed(new FeedSource(stash.Id, FeedProvider.Stash) { Media = media })).LoadNextAsync();
        Assert.Equal(titles, page.Items.Select(item => item.Title));
        Assert.All(page.Items, item => Assert.Equal(item.Title != "Images1", item.IsVideo));
        var videoImage = page.Items.FirstOrDefault(item => item.Title == "Images2");
        if (videoImage is not null) Assert.Equal($"/media/stash/{stash.Id:D}/images/2", videoImage.MediaUrl.OriginalString);
        Assert.Empty(page.Errors);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task StashFilteredFullBatchStillAdvancesByRawCount()
    {
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            offset == 0 ? Enumerable.Range(1, 20).Select(index => StashFile(instance, kind, index) with { IsVideo = true }).ToArray()
            : [StashFile(instance, kind, 21)]);
        var page = await Service(clients).Start(Feed(new FeedSource(stash.Id, FeedProvider.Stash) { Media = FeedMedia.Images })).LoadNextAsync();
        Assert.Equal("Images21", Assert.Single(page.Items).Title);
        Assert.Contains("Stash:Images:20:20", clients.Calls);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("query", "Invalid Hydrus feed query.")]
    [InlineData("unsupported", "This system predicate is not supported in feed queries.")]
    [InlineData("limit", "Random search exceeds the 100,000 matching file limit.")]
    [InlineData("secret", "could not be loaded.")]
    public async Task ReportsOnlyKnownSafeHydrusErrors(string failure, string expected)
    {
        var clients = new Clients();
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => throw failure switch
        {
            "query" => new ArgumentException("Invalid Hydrus feed query.", "query"),
            "unsupported" => new NotSupportedException("This system predicate is not supported in feed queries."),
            "limit" => new InvalidDataException("Hydrus feed search exceeds the supported result size."),
            _ => new ArgumentException("secret remote input", "query")
        };
        var page = await Service(clients).Start(new FeedDefinition(Guid.NewGuid(), "Feed", FeedOrder.Random,
            [new FeedSource(hydrus.Id, FeedProvider.Hydrus)])).LoadNextAsync();
        Assert.Contains(expected, Assert.Single(page.Errors));
        Assert.DoesNotContain("secret", page.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData("system:unknown secret", "Unsupported Hydrus system predicate or syntax in feed query.")]
    [InlineData("system:inbox and system:archive", "Conflicting system predicates cannot be combined safely.")]
    [InlineData("not system:width > 1", "This system predicate cannot be negated.")]
    [InlineData("tag\\*", "This escaped tag cannot be represented by the Hydrus API.")]
    [InlineData("tag and", "Invalid Hydrus feed query.")]
    public async Task ReportsRealSavedHydrusQueryErrors(string query, string detail)
    {
        var clients = new Clients();
        clients.Hydrus = (instance, savedQuery, media, order, offset, limit, token) =>
        {
            HydrusFeedQuery.Parse(savedQuery);
            return Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        };
        var page = await Service(clients).Start(Feed(new FeedSource(hydrus.Id, FeedProvider.Hydrus) { HydrusQuery = query })).LoadNextAsync();
        Assert.Equal($"Hydrus source {hydrus.Name} {detail}", Assert.Single(page.Errors));
        Assert.DoesNotContain("secret", page.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FeedProvider.Stash)]
    [InlineData(FeedProvider.Hydrus)]
    [InlineData(FeedProvider.SwarmUI)]
    public async Task ReportsConfiguredInstanceNameAndSanitizesUnexpectedErrors(FeedProvider provider)
    {
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => throw new HttpRequestException("secret https://user:password@stash.test/");
        clients.Hydrus = (instance, query, media, order, offset, limit, token) => throw new NotSupportedException("Unsupported Hydrus system predicate or syntax in feed query. secret");
        clients.Swarm = (instance, source, order, offset, limit, token) => throw new InvalidDataException("secret https://user:password@swarm.test/");
        var (id, name) = provider switch
        {
            FeedProvider.Stash => (stash.Id, stash.Name),
            FeedProvider.Hydrus => (hydrus.Id, hydrus.Name),
            _ => (swarm.Id, swarm.Name)
        };
        var page = await Service(clients).Start(Feed(new FeedSource(id, provider) { Media = FeedMedia.Images })).LoadNextAsync();
        Assert.Equal($"{provider} source {name} could not be loaded.", Assert.Single(page.Errors));
    }

    [Theory]
    [InlineData(FeedProvider.Stash)]
    [InlineData(FeedProvider.Hydrus)]
    [InlineData(FeedProvider.SwarmUI)]
    public async Task ReportsMissingInstanceId(FeedProvider provider)
    {
        var missing = Guid.NewGuid();
        var page = await Service(new Clients()).Start(Feed(new FeedSource(missing, provider))).LoadNextAsync();
        Assert.Equal($"{provider} source {missing:D} instance is missing.", Assert.Single(page.Errors));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("performer")]
    public async Task ReportsSafeStashExclusionErrors(string entity)
    {
        var clients = new Clients();
        var detail = $"A Stash excluded {entity} could not be resolved. Check excluded {entity} names in the feed settings and the Stash connection.";
        clients.Stash = (instance, kind, source, order, offset, limit, token) => throw new InvalidDataException(detail);
        var page = await Service(clients).Start(Feed(new FeedSource(stash.Id, FeedProvider.Stash) { Media = FeedMedia.Images })).LoadNextAsync();
        Assert.Equal($"Stash source {stash.Name} {detail}", Assert.Single(page.Errors));
    }

    [Fact]
    public async Task ReportsKnownSwarmFolderErrorWithoutLeakingUnknownMessages()
    {
        var clients = new Clients();
        clients.Swarm = (instance, source, order, offset, limit, token) =>
            throw new ArgumentException("The SwarmUI feed folder must be within a configured search path.", "source");
        var page = await Service(clients).Start(Feed(new FeedSource(swarm.Id, FeedProvider.SwarmUI))).LoadNextAsync();
        Assert.Contains("folder must be within a configured search path", Assert.Single(page.Errors));
    }

    [Fact]
    public async Task CancellationDuringConcurrentRequestsPreservesCompletedItems()
    {
        var clients = new Clients();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>(
            kind == StashSearchKind.Scenes ? [StashFile(instance, kind, 1)] : []);
        var first = true;
        clients.Hydrus = async (instance, query, media, order, offset, limit, token) =>
        {
            if (first)
            {
                first = false;
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return [new HydrusFile(1, "hydrus", "image/png")];
        };
        var session = Service(clients).Start(Feed(new(stash.Id, FeedProvider.Stash), new(hydrus.Id, FeedProvider.Hydrus)));
        var loading = session.LoadNextAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        var page = await session.LoadNextAsync();
        Assert.Equal(new[] { "Scenes1", "hydrus" }, page.Items.Select(item => item.Title));
        Assert.Empty(page.Errors);
        Assert.False(page.HasMore);
    }

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
        Assert.Equal(image.MediaUrl, image.SourceUrl);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task StashSupportsSeparateTypesScenePreviewsMarkersGifsAndManagedBrowserUrls()
    {
        var managedStash = new StashInstance(Guid.NewGuid(), "Managed Stash",
            new Uri("http://127.0.0.1:9999/graphql"), new Uri("http://127.0.0.1:9999/"), "secret", @"D:\Stash", 9999);
        var clients = new Clients();
        clients.Stash = (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>(kind switch
        {
            StashSearchKind.Scenes => [new StashSearchResult("10", "Scene", kind, null, new Uri(instance.WebUiUrl, "scenes/10"), instance.Id, instance.Name)
            {
                Duration = TimeSpan.FromMinutes(3),
                Rating100 = 80,
                Path = "video.mp4"
            }],
            StashSearchKind.Markers => [new StashSearchResult("20", "Marker", kind, null, new Uri(instance.WebUiUrl, "scenes/10?t=45"), instance.Id, instance.Name)
            {
                SceneId = "10",
                MarkerStart = TimeSpan.FromSeconds(45),
                MarkerEnd = TimeSpan.FromSeconds(75)
            }],
            StashSearchKind.Images => [new StashSearchResult("30", "Anim", kind, null, new Uri(instance.WebUiUrl, "images/30"), instance.Id, instance.Name)
            {
                Path = "funny.gif",
                IsVideo = true,
                HasAnimatedPreview = true
            }],
            _ => []
        });

        var service = new FeedService(
            new StashStore([managedStash]), clients,
            new HydrusStore([]), clients,
            new SwarmStore([]), clients);

        var source = new FeedSource(managedStash.Id, FeedProvider.Stash)
        {
            IncludeStashImages = true,
            IncludeStashScenes = true,
            IncludeStashMarkers = true,
            UseStashScenePreviews = true
        };
        var page = await service.Start(Feed(source)).LoadNextAsync();

        Assert.Equal(3, page.Items.Count);
        var scene = Assert.Single(page.Items, item => item.MediaKind == "Scene");
        Assert.Equal($"/media/stash/{managedStash.Id:D}/scene-previews/10", scene.MediaUrl.OriginalString);
        Assert.Equal($"/media/stash/{managedStash.Id:D}/scene-webp/10", scene.FallbackMediaUrl?.OriginalString);
        Assert.Equal($"/managed/stash/{managedStash.Id:D}/scenes/10", scene.SourceUrl.OriginalString);
        Assert.Equal(4m, scene.RatingOutOfFive);
        Assert.Equal("MP4", scene.FileFormat);

        var marker = Assert.Single(page.Items, item => item.MediaKind == "Marker");
        Assert.Equal($"/media/stash/{managedStash.Id:D}/markers/10/20", marker.MediaUrl.OriginalString);
        Assert.Equal($"/media/stash/{managedStash.Id:D}/marker-previews/10/20", marker.FallbackMediaUrl?.OriginalString);
        Assert.Equal($"/managed/stash/{managedStash.Id:D}/scenes/10?t=45", marker.SourceUrl.OriginalString);
        Assert.True(marker.IsVideo);
        Assert.Equal(TimeSpan.FromSeconds(45), marker.MarkerStart);
        Assert.Equal(TimeSpan.FromSeconds(75), marker.MarkerEnd);

        var gif = Assert.Single(page.Items, item => item.MediaKind == "GIF");
        Assert.Equal($"/media/stash/{managedStash.Id:D}/image-previews/30", gif.MediaUrl.OriginalString);
        Assert.False(gif.IsVideo);
        Assert.True(gif.FallbackIsVideo);
        Assert.Equal($"/managed/stash/{managedStash.Id:D}/images/30", gif.SourceUrl.OriginalString);
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
        public ConcurrentQueue<string> Calls { get; } = new();
        public Func<StashInstance, StashSearchKind, FeedSource, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<StashSearchResult>>> Stash { get; set; } =
            (instance, kind, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<StashSearchResult>>([]);
        public Func<HydrusInstance, string, FeedMedia, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<HydrusFile>>> Hydrus { get; set; } =
            (instance, query, media, order, offset, limit, token) => Task.FromResult<IReadOnlyList<HydrusFile>>([]);
        public Func<SwarmUIInstance, FeedSource, FeedOrder, int, int, CancellationToken, Task<IReadOnlyList<SwarmUIFile>>> Swarm { get; set; } =
            (instance, source, order, offset, limit, token) => Task.FromResult<IReadOnlyList<SwarmUIFile>>([]);

        public Task<IReadOnlyList<StashSearchResult>> SearchFeedAsync(StashInstance instance, StashSearchKind kind, FeedSource source, FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default)
        { Calls.Enqueue($"Stash:{kind}:{offset}:{limit}"); return Stash(instance, kind, source, order, offset, limit, cancellationToken); }
        public Task<IReadOnlyList<HydrusFile>> SearchFeedAsync(HydrusInstance instance, string query, FeedMedia media, FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default)
        { Calls.Enqueue($"Hydrus:{offset}:{limit}"); return Hydrus(instance, query, media, order, offset, limit, cancellationToken); }
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