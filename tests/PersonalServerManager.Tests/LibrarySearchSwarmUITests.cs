using System.Collections.Concurrent;
using System.Net;
using PersonalServerManager.Hydrus;
using PersonalServerManager.RandomPlot;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LibrarySearchSwarmUITests
{
    private static readonly SwarmUIInstance Instance =
        new(Guid.NewGuid(), "Swarm", new Uri("http://swarm.example:7801/"), SwarmUIInstance.DefaultSearchPaths);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_MapsRandomPlotGroupsAndEscapesLinks(bool managed)
    {
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Plots", new Uri("https://plots.example/base/"),
            managed ? "installed" : null);
        var client = new FakeRandomPlotClient((_, query, _) =>
        {
            Assert.Equal("hero & villain", query);
            return Task.FromResult<IReadOnlyList<RandomPlotGroup>>(
                [new("A & B/#? ü", [new("Hero"), new("Villain")], true)]);
        });
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: client);

        var group = Assert.Single(await service.SearchAsync([], [], "  hero & villain  ", randomPlotInstances: [instance]));

        Assert.Equal("Random Plot Generator", group.Provider);
        Assert.Equal("Groups", group.Category);
        Assert.Equal(instance.BrowserUrl, group.SearchUrl!.OriginalString);
        var item = Assert.Single(group.Items);
        Assert.Equal(instance.BrowserUrl + "?g=A%20%26%20B%2F%23%3F%20%C3%BC", item.ItemUrl!.OriginalString);
        Assert.Equal(LibraryMediaType.RandomPlotGroup, item.MediaType);
        Assert.Equal("Hero\nVillain", item.Description);
        Assert.Equal(2, item.ContentCount);
        Assert.True(item.IsFavorite);
    }

    [Theory]
    [InlineData(LibrarySort.TitleAscending, 5)]
    [InlineData(LibrarySort.TitleDescending, 25)]
    public async Task SearchAsync_SortsRandomPlotGroupsBeforeLimiting(LibrarySort sort, int limit)
    {
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Plots", new Uri("https://plots.example/"));
        var client = new FakeRandomPlotClient((_, _, _) => Task.FromResult<IReadOnlyList<RandomPlotGroup>>(
            Enumerable.Range(0, 30).Reverse().Select(index => new RandomPlotGroup($"Group {index:00}", [], false)).ToArray()));
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: client);

        var group = Assert.Single(await service.SearchAsync([], [], "group", limit: limit, sort: sort, randomPlotInstances: [instance]));

        var expected = Enumerable.Range(0, 30).Select(index => $"Group {index:00}");
        if (sort == LibrarySort.TitleDescending) expected = expected.Reverse();
        Assert.Equal(expected.Take(limit), group.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task SearchAsync_RandomizesRandomPlotGroupsBeforeLimiting()
    {
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Plots", new Uri("https://plots.example/"));
        var client = new FakeRandomPlotClient((_, _, _) => Task.FromResult<IReadOnlyList<RandomPlotGroup>>(
            Enumerable.Range(0, 30).Select(index => new RandomPlotGroup($"Group {index:00}", [], false)).ToArray()));
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: client);
        var selected = new HashSet<string>();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var group = Assert.Single(await service.SearchAsync([], [], "group", sort: LibrarySort.Random, randomPlotInstances: [instance]));
            Assert.Equal(5, group.Items.Count);
            selected.UnionWith(group.Items.Select(item => item.Id));
        }

        Assert.True(selected.Count > 5);
    }

    [Fact]
    public async Task SearchAsync_SanitizesRandomPlotSourceErrors()
    {
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Plots", new Uri("https://plots.example/"));
        var client = new FakeRandomPlotClient((_, _, _) => throw new InvalidDataException("secret"));
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: client);

        var group = Assert.Single(await service.SearchAsync([], [], "query", randomPlotInstances: [instance]));

        Assert.False(group.BackendUnreachable);
        Assert.Equal("Search failed. Check the instance connection and try again.", group.Error);
    }

    [Fact]
    public async Task SearchAsync_IsolatesRandomPlotFailureAndPropagatesCancellation()
    {
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Plots", new Uri("https://plots.example/"));
        var client = new FakeRandomPlotClient((_, _, _) => throw new HttpRequestException("secret", null, null));
        var service = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: client);
        var stash = new StashInstance(Guid.NewGuid(), "Stash", new Uri("https://stash.example/graphql"), new Uri("https://stash.example/"), null);

        var groups = await service.SearchAsync([stash], [], "query", randomPlotInstances: [instance]);

        var failed = Assert.Single(groups, group => group.Provider == "Random Plot Generator");
        Assert.True(failed.BackendUnreachable);
        Assert.Equal("Backend is not reachable.", failed.Error);
        Assert.Contains(groups, group => group.Provider == "Stash" && group.Items.Count == 1);

        using var cancellation = new CancellationTokenSource();
        var waitingClient = new FakeRandomPlotClient(async (_, _, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        var waitingService = new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient(), randomPlotClient: waitingClient);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingService.SearchAsync([], [], "query",
            cancellation.Token, randomPlotInstances: [instance]));
    }

    private sealed class FakeRandomPlotClient(
        Func<RandomPlotInstance, string, CancellationToken, Task<IReadOnlyList<RandomPlotGroup>>> search) : IRandomPlotClient
    {
        public Task<IReadOnlyList<RandomPlotGroup>> GetGroupsAsync(RandomPlotInstance instance, string query,
            CancellationToken cancellationToken = default) => search(instance, query, cancellationToken);
    }

    [Fact]
    public async Task SearchAsync_MapsSwarmFilesToLibraryItems()
    {
        var created = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        var longPrompt = "A  cat\n\twearing a hat, " + new string('x', 200);
        SwarmUIFile[] files =
        [
            new("raw/2026-09-26/1117001-cat.png", SwarmUIMediaKind.Image)
            {
                Prompt = longPrompt,
                Model = "Qwen_Image_2",
                Width = 1024,
                Height = 768,
                CreatedAt = created,
                IsStarred = true,
                Metadata =
                [
                    new("prompt", longPrompt),
                    new("negativeprompt", "blurry"),
                    new("model", "Qwen_Image_2"),
                    new("seed", "504678709"),
                    new("steps", "20"),
                    new("cfgscale", "4"),
                    new("width", "1024"),
                    new("height", "768"),
                    new("custom_key", "value")
                ]
            },
            new("raw/2026-08-30/1236001-clip.mp4", SwarmUIMediaKind.Video) { Model = "minimax" }
        ];
        var client = new FakeSwarmClient((_, _, _, _) => Task.FromResult<IReadOnlyList<SwarmUIFile>>(files));

        var group = Assert.Single(await Service(client).SearchAsync([], [], "cat", swarmInstances: [Instance]));

        Assert.Equal(("SwarmUI", "Outputs", Instance.BaseUrl), (group.Provider, group.Category, group.SearchUrl));
        Assert.Null(group.Error);
        var image = group.Items[0];
        Assert.Equal(files[0].Path, image.Id);
        Assert.Equal(120, image.Title.Length);
        Assert.StartsWith("A cat wearing a hat, xxx", image.Title);
        Assert.EndsWith("...", image.Title);
        Assert.Equal("Qwen_Image_2 · 1024x768", image.Detail);
        Assert.Equal(SwarmUIPaths.CreateFileLink(Instance.Id, files[0].Path), image.ItemUrl);
        Assert.Equal(SwarmUIPaths.CreatePreviewLink(Instance.Id, files[0].Path), image.PreviewUrl);
        Assert.Equal(LibraryMediaType.Image, image.MediaType);
        Assert.Equal(created, image.CreatedAt);
        Assert.Equal(files[0].Path, image.Path);
        Assert.True(image.IsFavorite);
        Assert.Equal("PNG", image.FileFormat);
        Assert.Equal(string.Join('\n',
            longPrompt, "Negative prompt: blurry", "Model: Qwen_Image_2", "Seed: 504678709", "Steps: 20", "CFG scale: 4",
                "Size: 1024x768", "custom_key: value"),
            image.Description);

        var video = group.Items[1];
        Assert.Equal("1236001-clip", video.Title);
        Assert.Equal("minimax", video.Detail);
        Assert.Equal(LibraryMediaType.Video, video.MediaType);
        Assert.Equal("MP4", video.FileFormat);
        Assert.False(video.IsFavorite);
        Assert.Equal("Model: minimax", video.Description);
    }

    [Fact]
    public async Task SearchAsync_PassesFullQueryAndHonoursLimit()
    {
        var calls = new ConcurrentBag<(string Query, int Limit, LibrarySort Sort)>();
        var client = new FakeSwarmClient((_, query, limit, sort) =>
        {
            calls.Add((query, limit, sort));
            return Task.FromResult<IReadOnlyList<SwarmUIFile>>(Enumerable.Range(0, 30)
                .Select(index => new SwarmUIFile($"raw/{index}.png", SwarmUIMediaKind.Image)).ToArray());
        });

        var group = Assert.Single(await Service(client).SearchAsync([], [], "  cat, hat & dog  ", limit: 5,
            sort: LibrarySort.DateNewest, swarmInstances: [Instance]));

        Assert.Equal(("cat, hat & dog", 5, LibrarySort.DateNewest), Assert.Single(calls));
        Assert.Equal(5, group.Items.Count);
    }

    [Fact]
    public async Task SearchAsync_LimitsImagesAndVideosSeparately()
    {
        var client = new FakeSwarmClient((_, _, _, _) => Task.FromResult<IReadOnlyList<SwarmUIFile>>(
            Enumerable.Range(0, 7).Select(index => new SwarmUIFile($"raw/{index}.mp4", SwarmUIMediaKind.Video))
                .Concat(Enumerable.Range(0, 7).Select(index => new SwarmUIFile($"raw/{index}.png", SwarmUIMediaKind.Image)))
                .ToArray()));

        var group = Assert.Single(await Service(client).SearchAsync([], [], "a", limit: 5, swarmInstances: [Instance]));

        Assert.Equal(5, group.Items.Count(item => item.MediaType == LibraryMediaType.Video));
        Assert.Equal(5, group.Items.Count(item => item.MediaType == LibraryMediaType.Image));
    }

    [Fact]
    public async Task SearchAsync_IsolatesSwarmFailures()
    {
        var client = new FakeSwarmClient((_, _, _, _) => throw new InvalidDataException("bad"));
        var stash = new FakeStashClient();
        var stashInstance = new StashInstance(Guid.NewGuid(), "Stash", new Uri("https://stash.example/graphql"), new Uri("https://stash.example/"), null);

        var groups = await Service(client, stash).SearchAsync([stashInstance], [], "cat", swarmInstances: [Instance]);

        var swarm = Assert.Single(groups, group => group.Provider == "SwarmUI");
        Assert.NotNull(swarm.Error);
        Assert.False(swarm.BackendUnreachable);
        Assert.All(groups.Where(group => group.Provider == "Stash"), group => Assert.Null(group.Error));
        Assert.Contains(groups, group => group.Provider == "Stash" && group.Items.Count == 1);
    }

    [Fact]
    public async Task SearchAsync_MarksUnreachableSwarmBackend()
    {
        var client = new FakeSwarmClient((_, _, _, _) => throw new HttpRequestException("refused", null, statusCode: null));

        var group = Assert.Single(await Service(client).SearchAsync([], [], "cat", swarmInstances: [Instance]));

        Assert.True(group.BackendUnreachable);
        Assert.Equal("Backend is not reachable.", group.Error);
    }

    [Fact]
    public async Task SearchAsync_SkipsSwarmInstancesWithoutClient()
    {
        var groups = await new LibrarySearchService(new FakeStashClient(), new FakeHydrusClient())
            .SearchAsync([], [], "cat", swarmInstances: [Instance]);

        Assert.Empty(groups);
    }

    private static LibrarySearchService Service(ISwarmUIClient client, IStashGraphQlClient? stash = null) =>
        new(stash ?? new FakeStashClient(), new FakeHydrusClient(), swarmClient: client);

    private sealed class FakeSwarmClient(
        Func<SwarmUIInstance, string, int, LibrarySort, Task<IReadOnlyList<SwarmUIFile>>> search) : ISwarmUIClient
    {
        public Task<IReadOnlyList<SwarmUIFile>> SearchFeedAsync(SwarmUIInstance instance, PersonalServerManager.Feeds.FeedSource source,
            PersonalServerManager.Feeds.FeedOrder order, int offset, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SwarmUIFile>> SearchAsync(SwarmUIInstance instance, string query, int limit, LibrarySort sort,
            CancellationToken cancellationToken = default) => search(instance, query, limit, sort);

        public Task<SwarmUIObservation> ObserveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStashClient : IStashGraphQlClient
    {
        public Task<IReadOnlyList<StashSearchResult>> SearchAsync(StashInstance instance, StashSearchKind kind, string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StashSearchResult>>(kind == StashSearchKind.Scenes
                ? [new("1", "Scene", kind, null, new Uri("https://stash.example/scenes/1"), instance.Id, instance.Name)]
                : []);

        public Task<StashObservation> ObserveAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHydrusClient : IHydrusClient
    {
        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HydrusFile>>([]);

        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
