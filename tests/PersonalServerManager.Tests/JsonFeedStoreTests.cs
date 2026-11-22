using PersonalServerManager.Feeds;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class JsonFeedStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("psm-feeds-").FullName;

    [Fact]
    public async Task FeedsRoundTripAndCanBeReorderedAndRemoved()
    {
        var path = Path.Combine(_folder, "feeds.json");
        var first = new FeedDefinition(Guid.NewGuid(), "First", FeedOrder.Random,
            [new(Guid.NewGuid(), FeedProvider.Stash) { IncludeTags = ["Captioned"], ExcludeTags = ["Black and White"] },
             new(Guid.NewGuid(), FeedProvider.Hydrus) { HydrusQuery = "(blue eyes AND blonde hair) OR brunette" }]);
        var second = new FeedDefinition(Guid.NewGuid(), "Second", FeedOrder.Newest,
            [new(Guid.NewGuid(), FeedProvider.SwarmUI) { SwarmFolder = "raw/folder", SwarmQuery = "test", Media = FeedMedia.Images }]);
        var store = new JsonFeedStore(path);
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.ReorderAsync([second.Id, first.Id]);
        var loaded = await new JsonFeedStore(path).GetAllAsync();
        Assert.Equal([second.Id, first.Id], loaded.Select(feed => feed.Id));
        Assert.Equal("raw/folder", loaded[0].Sources[0].SwarmFolder);
        Assert.Equal(["Captioned"], loaded[1].Sources[0].IncludeTags);
        Assert.Equal(first.Sources[1].HydrusQuery, loaded[1].Sources[1].HydrusQuery);
        await store.RemoveAsync(second.Id);
        Assert.Equal(first.Id, Assert.Single(await store.GetAllAsync()).Id);
    }

    [Fact]
    public async Task InvalidInputAndCorruptFileAreNotWritten()
    {
        var path = Path.Combine(_folder, "feeds.json");
        var store = new JsonFeedStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(Guid.NewGuid(), "Bad", FeedOrder.Newest,
            [new(Guid.NewGuid(), FeedProvider.SwarmUI) { SwarmFolder = "../outside" }])));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(Guid.NewGuid(), "NoStashTypes", FeedOrder.Newest,
            [new(Guid.NewGuid(), FeedProvider.Stash) { IncludeStashImages = false, IncludeStashScenes = false, IncludeStashMarkers = false }])));
        await File.WriteAllTextAsync(path, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
        Assert.Equal("invalid", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StashContentTypeFlagsRoundTrip()
    {
        var path = Path.Combine(_folder, "feeds.json");
        var feed = new FeedDefinition(Guid.NewGuid(), "Stash Markers", FeedOrder.Random,
            [new(Guid.NewGuid(), FeedProvider.Stash)
            {
                IncludeStashImages = false,
                IncludeStashScenes = true,
                IncludeStashMarkers = true,
                UseStashScenePreviews = true
            }]);
        var store = new JsonFeedStore(path);
        await store.SaveAsync(feed);
        var loaded = Assert.Single(await new JsonFeedStore(path).GetAllAsync());
        var source = Assert.Single(loaded.Sources);
        Assert.False(source.StashImagesEnabled);
        Assert.True(source.StashScenesEnabled);
        Assert.True(source.StashMarkersEnabled);
        Assert.True(source.UseStashScenePreviews);
    }

    public void Dispose() => Directory.Delete(_folder, true);
}