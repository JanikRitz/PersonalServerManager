using System.Net;
using System.Text;
using System.Text.Json;
using PersonalServerManager.Feeds;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class StashGraphQlClientTests
{
    [Theory]
    [InlineData(StashSearchKind.Scenes, "findScenes", "scenes", 5)]
    [InlineData(StashSearchKind.Images, "findImages", "images", 5)]
    [InlineData(StashSearchKind.Performers, "findPerformers", "performers", 5)]
    [InlineData(StashSearchKind.Scenes, "findScenes", "scenes", 25)]
    [InlineData(StashSearchKind.Images, "findImages", "images", 25)]
    [InlineData(StashSearchKind.Performers, "findPerformers", "performers", 25)]
    [InlineData(StashSearchKind.Studios, "findStudios", "studios", 5)]
    [InlineData(StashSearchKind.Tags, "findTags", "tags", 5)]
    [InlineData(StashSearchKind.Markers, "findSceneMarkers", "scene_markers", 5)]
    [InlineData(StashSearchKind.Galleries, "findGalleries", "galleries", 5)]
    [InlineData(StashSearchKind.Groups, "findGroups", "groups", 5)]
    [InlineData(StashSearchKind.Studios, "findStudios", "studios", 25)]
    [InlineData(StashSearchKind.Tags, "findTags", "tags", 25)]
    [InlineData(StashSearchKind.Markers, "findSceneMarkers", "scene_markers", 25)]
    [InlineData(StashSearchKind.Galleries, "findGalleries", "galleries", 25)]
    [InlineData(StashSearchKind.Groups, "findGroups", "groups", 25)]
    public async Task SearchAsync_RequestsAndClampsSelectedLimit(StashSearchKind kind, string field, string collection, int limit)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            var filter = payload.RootElement.GetProperty("variables").GetProperty("filter");
            Assert.Equal(limit, filter.GetProperty("per_page").GetInt32());
            Assert.Equal(1, filter.GetProperty("page").GetInt32());
            Assert.Contains($"{field}(filter: $filter)", payload.RootElement.GetProperty("query").GetString());
            Assert.Contains("created_at updated_at", payload.RootElement.GetProperty("query").GetString());
            var items = Enumerable.Range(1, 30).Select(index => new { id = index.ToString(), title = "Title", name = "Name", seconds = 12.5, scene = new { id = "99" } });
            return Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [collection] = items } }
            }));
        })));

        var results = await client.SearchAsync(Instance("test-key"), kind, "query", limit);

        Assert.Equal(limit, results.Count);
        Assert.Equal(limit.ToString(), results[^1].Id);
    }

    [Theory]
    [InlineData(StashSearchKind.Scenes, LibrarySort.TitleAscending, "title", "ASC")]
    [InlineData(StashSearchKind.Images, LibrarySort.TitleDescending, "title", "DESC")]
    [InlineData(StashSearchKind.Performers, LibrarySort.TitleDescending, "name", "DESC")]
    [InlineData(StashSearchKind.Tags, LibrarySort.TitleAscending, "name", "ASC")]
    [InlineData(StashSearchKind.Scenes, LibrarySort.DateNewest, "created_at", "DESC")]
    [InlineData(StashSearchKind.Performers, LibrarySort.DateOldest, "created_at", "ASC")]
    [InlineData(StashSearchKind.Scenes, LibrarySort.Random, null, "ASC")]
    [InlineData(StashSearchKind.Scenes, LibrarySort.Default, null, null)]
    public async Task SearchAsync_SendsNativeSortInFilter(StashSearchKind kind, LibrarySort sort, string? expectedSort, string? expectedDirection)
    {
        var calls = 0;
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            calls++;
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            var filter = payload.RootElement.GetProperty("variables").GetProperty("filter");
            Assert.Equal("query", filter.GetProperty("q").GetString());
            Assert.Equal(25, filter.GetProperty("per_page").GetInt32());
            Assert.Equal(1, filter.GetProperty("page").GetInt32());
            if (sort == LibrarySort.Default)
            {
                Assert.False(filter.TryGetProperty("sort", out var _));
                Assert.False(filter.TryGetProperty("direction", out var _));
            }
            else
            {
                var actualSort = filter.GetProperty("sort").GetString();
                if (sort == LibrarySort.Random) Assert.Matches(@"^random_\d+$", actualSort);
                else Assert.Equal(expectedSort, actualSort);
                Assert.Equal(expectedDirection, filter.GetProperty("direction").GetString());
            }

            return Json("""{"data":{"findScenes":{"scenes":[]},"findImages":{"images":[]},"findPerformers":{"performers":[]},"findTags":{"tags":[]}}}""");
        })));

        await client.SearchAsync(Instance(null), kind, "query", 25, sort);

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(26)]
    public async Task SearchAsync_RejectsUnsupportedLimitsWithoutSending(int limit)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send."))));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync(Instance(null), StashSearchKind.Images, "query", limit));
    }

    [Fact]
    public async Task SearchAsync_MapsDatesAndFilePathWithMissingTitleFallback()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("files { path duration } rating100 created_at updated_at", body);
            return Json("""{"data":{"findScenes":{"scenes":[{"id":"42","title":null,"files":[{"path":"/library/scene.mp4","duration":62.5}],"rating100":85,"created_at":"2020-01-02T03:04:05Z","updated_at":"2022-05-06T07:08:09Z"},{"id":"43","title":"Other","rating100":0,"created_at":"invalid","updated_at":null}]}}}""");
        })));

        var results = await client.SearchAsync(Instance(null), StashSearchKind.Scenes, "query");

        Assert.Equal("/library/scene.mp4", results[0].Title);
        Assert.Equal("/library/scene.mp4", results[0].Path);
        Assert.Equal(85, results[0].Rating100);
        Assert.Equal(TimeSpan.FromSeconds(62.5), results[0].Duration);
        Assert.Null(results[1].Rating100);
        Assert.Null(results[1].Duration);
        Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"), results[0].CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2022-05-06T07:08:09Z"), results[0].UpdatedAt);
        Assert.Null(results[1].CreatedAt);
        Assert.Null(results[1].UpdatedAt);
        Assert.Null(results[1].Path);
    }

    [Fact]
    public async Task SearchAsync_MapsAnimatedImageVisualFilePath()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("visual_files { __typename ... on ImageFile { path } ... on VideoFile { path } }", body);
            return Json("""{"data":{"findImages":{"images":[{"id":"433868","title":"Animated image","files":[],"visual_files":[{"path":"/library/animated.gif"}]}]}}}""");
        })));

        var image = Assert.Single(await client.SearchAsync(Instance(null), StashSearchKind.Images, "animated"));

        Assert.Equal("/library/animated.gif", image.Path);
        Assert.False(image.IsVideo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageResults_ClassifyVisualFileTypesInSearchAndFeed(bool feed)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("visual_files { __typename", body);
            return Json("""{"data":{"findImages":{"images":[{"id":"1","visual_files":[{"__typename":"ImageFile","path":"still.jpg"}]},{"id":"2","visual_files":[{"__typename":"VideoFile","path":"clip.mp4"}]},{"id":"3","visual_files":[{"__typename":"ImageFile"},{"__typename":"VideoFile"}]},{"id":"4","visual_files":[]}]}}}""");
        })));
        var instance = Instance(null);
        var results = feed
            ? await client.SearchFeedAsync(instance, StashSearchKind.Images, new FeedSource(instance.Id, FeedProvider.Stash), FeedOrder.Newest, 0, 5)
            : await client.SearchAsync(instance, StashSearchKind.Images, "query");

        Assert.Equal(new[] { false, true, true, false }, results.Select(result => result.IsVideo));
        Assert.Equal("clip.mp4", results[1].Path);
    }

    [Fact]
    public async Task SearchAsync_OnlyEnablesCanonicalAnimatedPreviews()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("paths { thumbnail preview }", body);
            return Json("""{"data":{"findImages":{"images":[{"id":"42","paths":{"preview":"https://stash.example/prefix/image/42/preview?apikey=private"}},{"id":"43","paths":{"preview":null}},{"id":"44","paths":{"preview":"https://evil.example/image/44/preview"}}]}}}""");
        })));
        var instance = Instance(null) with { WebUiUrl = new Uri("https://stash.example/prefix/") };

        var images = await client.SearchAsync(instance, StashSearchKind.Images, "query");

        Assert.True(images[0].HasAnimatedPreview);
        Assert.False(images[1].HasAnimatedPreview);
        Assert.False(images[2].HasAnimatedPreview);
    }

    [Fact]
    public async Task SearchAsync_MapsResultsAndUsesInstanceAuthentication()
    {
        var handler = new StubHandler((request, body) =>
        {
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            Assert.Contains("findScenes", body);
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal(5, payload.RootElement.GetProperty("variables").GetProperty("filter").GetProperty("per_page").GetInt32());
            return Json("""
                {"data":{"findScenes":{"count":1,"scenes":[{"id":"42","title":"A scene","paths":{"screenshot":"/scene/42/screenshot"}}]}}}
                """);
        });
        var client = new StashGraphQlClient(new HttpClient(handler));
        var instance = Instance("test-key");

        var results = await client.SearchAsync(instance, StashSearchKind.Scenes, "scene");

        var result = Assert.Single(results);
        Assert.Equal("A scene", result.Title);
        Assert.True(result.IsVideo);
        Assert.Equal(instance.Id, result.InstanceId);
        Assert.Equal("https://stash.example/scenes/42", result.ItemUrl.AbsoluteUri);
        Assert.Equal(new Uri("https://stash.example/scene/42/screenshot"), result.ThumbnailUrl);
    }

    [Fact]
    public async Task ObserveAsync_ReportsAnUnreachableInstanceWithoutThrowing()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new StashGraphQlClient(new HttpClient(handler));

        var observation = await client.ObserveAsync(Instance(null));

        Assert.False(observation.IsReachable);
        Assert.Contains("503", observation.Message);
    }

    [Fact]
    public async Task SearchAsync_MapsImagesAndPerformers()
    {
        var handler = new StubHandler((_, body) => body.Contains("findImages", StringComparison.Ordinal)
            ? Json("""
                {"data":{"findImages":{"count":1,"images":[{"id":"7","title":"A still","paths":{"thumbnail":"/image/7/thumbnail"}}]}}}
                """)
            : Json("""
                {"data":{"findPerformers":{"count":1,"performers":[{"id":"9","name":"A performer","image_path":"/performer/9/image"}]}}}
                """));
        var client = new StashGraphQlClient(new HttpClient(handler));
        var instance = Instance(null);

        var image = Assert.Single(await client.SearchAsync(instance, StashSearchKind.Images, "still"));
        var performer = Assert.Single(await client.SearchAsync(instance, StashSearchKind.Performers, "performer"));

        Assert.Equal("A still", image.Title);
        Assert.Equal(new Uri("https://stash.example/images/7"), image.ItemUrl);
        Assert.Equal(new Uri("https://stash.example/image/7/thumbnail"), image.ThumbnailUrl);
        Assert.Equal("A performer", performer.Title);
        Assert.Equal(new Uri("https://stash.example/performers/9"), performer.ItemUrl);
        Assert.Equal(instance.Id, performer.InstanceId);
    }

    [Theory]
    [InlineData(StashSearchKind.Studios, "findStudios", "studios", "id name image_path rating100 favorite", "studio/42/image")]
    [InlineData(StashSearchKind.Tags, "findTags", "tags", "id name image_path favorite", "tag/42/image")]
    [InlineData(StashSearchKind.Galleries, "findGalleries", "galleries", "id title paths { cover } files { path } rating100", "gallery/42/cover")]
    [InlineData(StashSearchKind.Groups, "findGroups", "groups", "id name front_image_path scenes { id } rating100", "group/42/frontimage")]
    public async Task SearchAsync_MapsNewCollectionsAndTrustedPreviewPaths(
        StashSearchKind kind, string field, string collection, string selection, string previewPath)
    {
        var instance = Instance("test-key") with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var request = System.Text.Json.JsonDocument.Parse(body);
            Assert.Contains($"{collection} {{ {selection} created_at updated_at }}", request.RootElement.GetProperty("query").GetString());
            Assert.Equal(5, request.RootElement.GetProperty("variables").GetProperty("filter").GetProperty("per_page").GetInt32());
            var thumbnail = new Uri(instance.WebUiUrl, previewPath).AbsoluteUri + "?apikey=private";
            var item = new { id = "42", name = "A name", title = "A title", image_path = thumbnail, front_image_path = thumbnail,
                paths = new { cover = thumbnail }, files = new[] { new { path = "one.jpg" }, new { path = "two.jpg" } },
                scenes = new[] { new { id = "1" }, new { id = "2" }, new { id = "3" } } };
            return Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [collection] = new[] { item } } }
            }));
        })));

        var result = Assert.Single(await client.SearchAsync(instance, kind, "query"));

        Assert.Equal(kind == StashSearchKind.Galleries ? "A title" : "A name", result.Title);
        Assert.Equal(new Uri(instance.WebUiUrl, $"{collection}/42"), result.ItemUrl);
        Assert.Equal(new Uri(instance.WebUiUrl, previewPath), result.ThumbnailUrl);
        Assert.Equal(instance.Id, result.InstanceId);
        Assert.Equal(instance.Name, result.InstanceName);
        Assert.Equal(kind is StashSearchKind.Galleries ? 2 : kind is StashSearchKind.Groups ? 3 : null, result.ContentCount);
    }

    [Theory]
    [InlineData("A marker", "A marker", 145.5)]
    [InlineData("", "Primary tag", null)]
    public async Task SearchAsync_MarkersLinkToSceneTimestampWithoutInventingPreview(string title, string expectedTitle, double? endSeconds)
    {
        var instance = Instance(null) with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("scene_markers { id title seconds end_seconds scene { id } primary_tag { name } screenshot created_at updated_at }", body);
            return Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new { findSceneMarkers = new { scene_markers = new[] { new { id = "42", title, seconds = 123.75, end_seconds = endSeconds, scene = new { id = "9" }, primary_tag = new { name = "Primary tag" }, screenshot = "https://evil.example/thumbnail" } } } }
            }));
        })));
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var result = Assert.Single(await client.SearchAsync(instance, StashSearchKind.Markers, "query"));
            Assert.Equal(expectedTitle, result.Title);
            Assert.Equal(TimeSpan.FromSeconds(123.75), result.MarkerStart);
            Assert.Equal(endSeconds.HasValue ? TimeSpan.FromSeconds(endSeconds.Value) : null, result.MarkerEnd);
            Assert.Equal(new Uri("https://stash.example/prefix/scenes/9?t=123.75"), result.ItemUrl);
            Assert.Null(result.ThumbnailUrl);
            Assert.Equal("42", result.Id);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("https://stash.example/prefix/scene/9/scene_marker/42/screenshot?apikey=private", true)]
    [InlineData("/prefix/scene/9/scene_marker/42/screenshot", true)]
    [InlineData("https://evil.example/prefix/scene/9/scene_marker/42/screenshot", false)]
    [InlineData("https://user:secret@stash.example/prefix/scene/9/scene_marker/42/screenshot", false)]
    [InlineData("/prefix/scene/9/screenshot", false)]
    [InlineData("/prefix/scene/9/scene_marker/43/screenshot", false)]
    [InlineData("/prefix/scene/8/scene_marker/42/screenshot", false)]
    [InlineData(null, false)]
    public async Task SearchAsync_MarkersOnlyAcceptCanonicalScreenshot(string? screenshot, bool trusted)
    {
        var instance = Instance(null) with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((request, body) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("primary_tag { name } screenshot", body);
            return Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new { findSceneMarkers = new { scene_markers = new[] { new { id = "42", title = "Marker", seconds = 12.5, scene = new { id = "9" }, screenshot } } } }
            }));
        })));
        var marker = Assert.Single(await client.SearchAsync(instance, StashSearchKind.Markers, "query"));
        Assert.Equal(trusted ? new Uri(instance.WebUiUrl, "scene/9/scene_marker/42/screenshot") : null, marker.ThumbnailUrl);
        Assert.Equal("42", marker.Id);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("[{\"id\":\"42\",\"scene\":{\"id\":\"9\"}}]", "9")]
    public async Task GetMarkerSceneIdAsync_UsesBoundedSchemaAndConfiguredCredential(string markers, string? expected)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((request, body) =>
        {
            Assert.Equal(Instance("test-key").GraphQlEndpoint, request.RequestUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal("42", payload.RootElement.GetProperty("variables").GetProperty("id").GetString());
            Assert.Equal("query MarkerScene($id: ID!) { findSceneMarkers(ids: [$id], filter: { per_page: 1, page: 1 }) { scene_markers { id scene { id } } } }", payload.RootElement.GetProperty("query").GetString());
            return Json("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":" + markers + "}}}");
        })));
        Assert.Equal(expected, await client.GetMarkerSceneIdAsync(Instance("test-key"), "42"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"errors\":[{\"message\":\"Failed\"}]}")]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"43\",\"scene\":{\"id\":\"9\"}}]}}}")]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"42\",\"scene\":{\"id\":\"../9\"}}]}}}")]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"42\",\"scene\":null}]}}}")]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{},{}]}}}")]
    public async Task GetMarkerSceneIdAsync_RejectsInvalidOrMismatchedResolution(string response)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => Json(response))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetMarkerSceneIdAsync(Instance(null), "42"));
    }

    [Fact]
    public async Task GetMarkerSceneIdAsync_RejectsOversizedResponseAndPreCanceledRequests()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => Json(new string(' ', 65537)))));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMarkerSceneIdAsync(Instance(null), "42"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMarkerSceneIdAsync(Instance(null), "42", cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMarkerSceneIdAsync(Instance(null), "../42"));
    }

    [Theory]
    [InlineData("{\"id\":\"42\",\"seconds\":-1,\"scene\":{\"id\":\"9\"}}")]
    [InlineData("{\"id\":\"42\",\"seconds\":\"123\",\"scene\":{\"id\":\"9\"}}")]
    [InlineData("{\"id\":\"42\",\"seconds\":123,\"scene\":{}}")]
    public async Task SearchAsync_RejectsInvalidMarkerNavigation(string item)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) =>
            Json("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[" + item + "]}}}"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Instance(null), StashSearchKind.Markers, "query"));
    }

    [Theory]
    [InlineData("https://evil.example/tag/42/image")]
    [InlineData("//evil.example/tag/42/image")]
    [InlineData("https://user:secret@stash.example/tag/42/image")]
    [InlineData("http://stash.example/tag/42/image")]
    [InlineData("/tag/43/image")]
    [InlineData("/arbitrary/image")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public async Task SearchAsync_DoesNotReturnUntrustedOrMissingThumbnail(string? imagePath)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new { findTags = new { tags = new[] { new { id = "42", name = "A tag", image_path = imagePath } } } }
        })))));
        Assert.Null(Assert.Single(await client.SearchAsync(Instance(null), StashSearchKind.Tags, "tag")).ThumbnailUrl);
    }

    [Fact]
    public async Task SearchAsync_PreservesEnumValuesAndPreCanceledRequests()
    {
        Assert.Equal(new[] { "Scenes", "Images", "Performers", "Studios", "Tags", "Markers", "Galleries", "Groups" }, Enum.GetNames<StashSearchKind>());
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send."))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var kind in Enum.GetValues<StashSearchKind>())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync(Instance(null), kind, "query", cancellation.Token));
    }

    [Theory]
    [InlineData(StashSearchKind.Scenes, "findScenes", "scenes", "scene_filter", "SceneFilterType")]
    [InlineData(StashSearchKind.Images, "findImages", "images", "image_filter", "ImageFilterType")]
    public async Task SearchFeedAsync_CombinesExactIncludesAndExcludesOnServer(
        StashSearchKind kind, string field, string collection, string argument, string filterType)
    {
        var calls = 0;
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((request, body) =>
        {
            calls++;
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            using var payload = JsonDocument.Parse(body);
            var root = payload.RootElement;
            var query = root.GetProperty("query").GetString()!;
            var variables = root.GetProperty("variables");
            if (query.Contains("FeedNames"))
            {
                var name = variables.GetProperty("criteria").GetProperty("name");
                Assert.Equal("EQUALS", name.GetProperty("modifier").GetString());
                Assert.Contains("per_page: -1", query);
                var value = name.GetProperty("value").GetString()!;
                var id = value switch { "included tag" or "INCLUDED TAG" => "1", "excluded tag" => "2", "included performer" => "3", _ => "4" };
                var isTag = query.Contains("findTags");
                return Json(JsonSerializer.Serialize(new { data = new Dictionary<string, object> {
                    [isTag ? "findTags" : "findPerformers"] = new Dictionary<string, object> {
                        [isTag ? "tags" : "performers"] = new[] { new { id, name = value.ToUpperInvariant() }, new { id = "999", name = value + " suffix" } } } } }));
            }
            Assert.Contains($"$criteria: {filterType}", query);
            Assert.Contains($"{field}(filter: $filter, {argument}: $criteria)", query);
            var filter = variables.GetProperty("filter");
            Assert.Equal("created_at", filter.GetProperty("sort").GetString());
            Assert.Equal("DESC", filter.GetProperty("direction").GetString());
            var criteria = variables.GetProperty("criteria");
            foreach (var (property, includeId, excludeId) in new[] { ("tags", "1", "2"), ("performers", "3", "4") })
            {
                var criterion = criteria.GetProperty(property);
                Assert.Equal("INCLUDES", criterion.GetProperty("modifier").GetString());
                Assert.Equal(includeId, Assert.Single(criterion.GetProperty("value").EnumerateArray()).GetString());
                Assert.Equal(excludeId, Assert.Single(criterion.GetProperty("excludes").EnumerateArray()).GetString());
                if (property == "tags") Assert.Equal(0, criterion.GetProperty("depth").GetInt32());
            }
            return Json(JsonSerializer.Serialize(new { data = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [collection] = new[] { new { id = "42", title = "Result" } } } } }));
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash)
        {
            IncludeTags = [" included tag ", "INCLUDED TAG"], ExcludeTags = ["excluded tag"],
            IncludePerformers = ["included performer"], ExcludePerformers = ["excluded performer"]
        };

        IStashGraphQlClient contract = client;
        var result = Assert.Single(await contract.SearchFeedAsync(Instance("test-key"), kind, source, FeedOrder.Newest, 0, 5));
        Assert.Equal("42", result.Id);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(6, calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SearchFeedAsync_LooksUpUnicodeCaseVariantsIndependently(bool tags, bool missingSecondExclusion)
    {
        const string upper = "\u00c4";
        const string lower = "\u00e4";
        var lookups = new List<string>();
        var searched = false;
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            var variables = root.GetProperty("variables");
            if (root.GetProperty("query").GetString()!.Contains("FeedNames"))
            {
                var name = variables.GetProperty("criteria").GetProperty("name").GetProperty("value").GetString()!;
                lookups.Add(name);
                var items = missingSecondExclusion && name == lower
                    ? Array.Empty<object>()
                    : new[] { new { id = name == upper ? "1" : "2", name } };
                return Json(JsonSerializer.Serialize(new { data = new Dictionary<string, object> {
                    [tags ? "findTags" : "findPerformers"] = new Dictionary<string, object> {
                        [tags ? "tags" : "performers"] = items } } }));
            }
            searched = true;
            var criterion = variables.GetProperty("criteria").GetProperty(tags ? "tags" : "performers");
            Assert.Equal(new[] { "1", "2" }, criterion.GetProperty("value").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { "1", "2" }, criterion.GetProperty("excludes").EnumerateArray().Select(value => value.GetString()));
            return Json("""{"data":{"findScenes":{"scenes":[]}}}""");
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash)
        {
            IncludeTags = tags ? [upper, lower, upper] : [],
            IncludePerformers = tags ? [] : [upper, lower, upper],
            ExcludeTags = tags ? [upper, lower, upper] : [],
            ExcludePerformers = tags ? [] : [upper, lower, upper]
        };

        if (missingSecondExclusion)
            await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5));
        else
            await client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5);

        Assert.Equal(new[] { upper, lower, upper, lower }, lookups);
        Assert.Equal(!missingSecondExclusion, searched);
    }

    [Theory]
    [InlineData(FeedOrder.Newest, "created_at", "DESC")]
    [InlineData(FeedOrder.Oldest, "created_at", "ASC")]
    [InlineData(FeedOrder.Random, null, "ASC")]
    public async Task SearchFeedAsync_UsesStableSortAndSlicesUnalignedPages(FeedOrder order, string? expectedSort, string direction)
    {
        var sorts = new List<string>();
        var pages = new List<int>();
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var payload = JsonDocument.Parse(body);
            var variables = payload.RootElement.GetProperty("variables");
            Assert.Empty(variables.GetProperty("criteria").EnumerateObject());
            var filter = variables.GetProperty("filter");
            Assert.Equal(3, filter.GetProperty("per_page").GetInt32());
            Assert.Equal(direction, filter.GetProperty("direction").GetString());
            var sort = filter.GetProperty("sort").GetString()!;
            if (expectedSort is null) Assert.Matches(@"^random_\d+$", sort);
            else Assert.Equal(expectedSort, sort);
            sorts.Add(sort);
            var page = filter.GetProperty("page").GetInt32();
            pages.Add(page);
            var items = Enumerable.Range((page - 1) * 3 + 1, 3).Select(index => new { id = index.ToString(), title = "Image" });
            return Json(JsonSerializer.Serialize(new { data = new { findImages = new { images = items } } }));
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { IncludeTags = ["", "  "] };

        var first = await client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, order, 2, 3);
        var second = await client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, order, 5, 3);

        Assert.Equal(new[] { "3", "4", "5" }, first.Select(item => item.Id));
        Assert.Equal(new[] { "6", "7", "8" }, second.Select(item => item.Id));
        Assert.Equal(new[] { 1, 2, 2, 3 }, pages);
        Assert.Single(sorts.Distinct());
    }

    [Fact]
    public async Task SearchFeedAsync_UnknownIncludesNeverBroadenResults()
    {
        var calls = 0;
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            calls++;
            return body.Contains("FeedNames")
                ? Json("""{"data":{"findTags":{"tags":[{"id":"9","name":"not exact"}]}}}""")
                : Json("""{"data":{"findScenes":{"scenes":[{"id":"1"}]}}}""");
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash)
        {
            IncludeTags = ["missing"]
        };

        var results = await client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5);
        Assert.Empty(results);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SearchFeedAsync_SupportsMarkersWithSceneIdAndPreviews()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("findSceneMarkers(filter: $filter, scene_marker_filter: $criteria)", body);
            return Json("""{"data":{"findSceneMarkers":{"scene_markers":[{"id":"42","title":"Highlight","seconds":30,"end_seconds":48,"scene":{"id":"9","rating100":90},"primary_tag":{"name":"Tag"},"screenshot":"https://stash.example/prefix/scene/9/scene_marker/42/screenshot","stream":"https://stash.example/prefix/scene/9/scene_marker/42/stream","preview":"https://stash.example/prefix/scene/9/scene_marker/42/preview"}]}}}""");
        })));
        var instance = Instance(null) with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var source = new FeedSource(instance.Id, FeedProvider.Stash) { IncludeStashMarkers = true };

        var marker = Assert.Single(await client.SearchFeedAsync(instance, StashSearchKind.Markers, source, FeedOrder.Newest, 0, 5));
        Assert.Equal("42", marker.Id);
        Assert.Equal("9", marker.SceneId);
        Assert.Equal("Highlight", marker.Title);
        Assert.Equal(TimeSpan.FromSeconds(30), marker.MarkerStart);
        Assert.Equal(TimeSpan.FromSeconds(48), marker.MarkerEnd);
        Assert.Equal(90, marker.Rating100);
        Assert.True(marker.HasAnimatedPreview);
        Assert.True(marker.HasPreviewVideo);
        Assert.Equal("https://stash.example/prefix/scenes/9?t=30", marker.ItemUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchFeedAsync_RejectsAnyMissingExcludedNameWithoutSearching(bool tags)
    {
        var calls = 0;
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            calls++;
            using var request = JsonDocument.Parse(body);
            Assert.Contains("FeedNames", request.RootElement.GetProperty("query").GetString());
            var name = request.RootElement.GetProperty("variables").GetProperty("criteria").GetProperty("name").GetProperty("value").GetString();
            var collection = tags ? "tags" : "performers";
            var field = tags ? "findTags" : "findPerformers";
            var items = name == "known" ? new[] { new { id = "1", name = "KNOWN" } } : Array.Empty<object>();
            return Json(JsonSerializer.Serialize(new { data = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [collection] = items } } }));
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash)
        {
            ExcludeTags = tags ? ["known", "secret-missing"] : [],
            ExcludePerformers = tags ? [] : ["known", "secret-missing"]
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchFeedAsync(Instance("private-key"), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5));
        Assert.Contains(tags ? "excluded tag" : "excluded performer", exception.Message);
        Assert.Contains("feed settings", exception.Message);
        Assert.DoesNotContain("secret-missing", exception.Message);
        Assert.DoesNotContain("private-key", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SearchFeedAsync_PartiallyResolvedIncludesUseMatchingIds()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            var variables = root.GetProperty("variables");
            if (root.GetProperty("query").GetString()!.Contains("FeedNames"))
            {
                var name = variables.GetProperty("criteria").GetProperty("name").GetProperty("value").GetString();
                return name == "known" ? Json("""{"data":{"findTags":{"tags":[{"id":"1","name":"KNOWN"}]}}}""") : Json("""{"data":{"findTags":{"tags":[]}}}""");
            }
            Assert.Equal("1", Assert.Single(variables.GetProperty("criteria").GetProperty("tags").GetProperty("value").EnumerateArray()).GetString());
            return Json("""{"data":{"findScenes":{"scenes":[{"id":"42"}]}}}""");
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { IncludeTags = ["missing", "known"] };

        Assert.Equal("42", Assert.Single(await client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5)).Id);
    }

    [Fact]
    public async Task SearchFeedAsync_MissingIncludeDoesNotHideMissingExclusion()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) => body.Contains("findTags")
            ? Json("""{"data":{"findTags":{"tags":[]}}}""")
            : Json("""{"data":{"findPerformers":{"performers":[]}}}"""))));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { IncludeTags = ["missing"], ExcludePerformers = ["missing"] };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, FeedOrder.Newest, 0, 5));
        Assert.Contains("excluded performer", exception.Message);
    }

    [Fact]
    public async Task SearchFeedAsync_ExcludesOnlyExactNamesAndStopsAtShortPage()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var payload = JsonDocument.Parse(body);
            var query = payload.RootElement.GetProperty("query").GetString()!;
            var variables = payload.RootElement.GetProperty("variables");
            if (query.Contains("FeedNames"))
            {
                Assert.Equal("a_%", variables.GetProperty("criteria").GetProperty("name").GetProperty("value").GetString());
                return Json("""{"data":{"findTags":{"tags":[{"id":"1","name":"a_%"},{"id":"2","name":"axb"},{"id":"3","name":"A_%"}]}}}""");
            }
            var tags = variables.GetProperty("criteria").GetProperty("tags");
            Assert.Equal("INCLUDES", tags.GetProperty("modifier").GetString());
            Assert.False(tags.TryGetProperty("value", out var _));
            Assert.Equal(new[] { "1", "3" }, tags.GetProperty("excludes").EnumerateArray().Select(value => value.GetString()));
            return Json("""{"data":{"findImages":{"images":[{"id":"5"},{"id":"6"}]}}}""");
        })));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { ExcludeTags = ["a_%"] };

        var results = await client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, FeedOrder.Oldest, 1, 3);

        Assert.Equal("6", Assert.Single(results).Id);
    }

    [Fact]
    public async Task SearchFeedAsync_ValidatesArgumentsMediaAndCancellationWithoutRequests()
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send."))));
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { Media = FeedMedia.Images };
        Assert.Empty(await client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5));
        Assert.Empty(await client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source with { Media = FeedMedia.Videos }, FeedOrder.Newest, 0, 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Tags, source, FeedOrder.Newest, 0, 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, FeedOrder.Newest, -1, 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, FeedOrder.Newest, 0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, (FeedOrder)999, 0, 5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Scenes, source, FeedOrder.Newest, 0, 5, cancellation.Token));
    }

    [Fact]
    public async Task SearchFeedAsync_RejectsMalformedLookupsAndGraphQlErrors()
    {
        var source = new FeedSource(Instance(null).Id, FeedProvider.Stash) { ExcludeTags = ["tag"] };
        foreach (var response in new[] { "{}", """{"errors":[{"message":"private-key bad query"}]}""", """{"data":{"findTags":{"tags":[{"id":null,"name":"tag"}]}}}""" })
        {
            var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => Json(response))));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchFeedAsync(Instance(null), StashSearchKind.Images, source, FeedOrder.Newest, 0, 5));
            Assert.Contains("excluded tag", exception.Message);
            Assert.Contains("Stash connection", exception.Message);
            Assert.DoesNotContain("private-key", exception.ToString());
        }
    }

    private static StashInstance Instance(string? apiKey) => new(
        Guid.Parse("c7a6a4c0-c79f-4e3b-8aa9-8665d0564cbd"),
        "Primary Stash",
        new Uri("https://stash.example/graphql"),
        new Uri("https://stash.example/"),
        apiKey);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond(request, body);
        }
    }
}