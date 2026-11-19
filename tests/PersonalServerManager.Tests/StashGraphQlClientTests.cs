using System.Net;
using System.Text;
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
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(26)]
    public async Task SearchAsync_RejectsUnsupportedLimitsWithoutSending(int limit)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send."))));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync(Instance(null), StashSearchKind.Images, "query", limit));
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
    [InlineData(StashSearchKind.Studios, "findStudios", "studios", "id name image_path", "studio/42/image")]
    [InlineData(StashSearchKind.Tags, "findTags", "tags", "id name image_path", "tag/42/image")]
    [InlineData(StashSearchKind.Galleries, "findGalleries", "galleries", "id title paths { cover }", "gallery/42/cover")]
    [InlineData(StashSearchKind.Groups, "findGroups", "groups", "id name front_image_path", "group/42/frontimage")]
    public async Task SearchAsync_MapsNewCollectionsAndTrustedPreviewPaths(
        StashSearchKind kind, string field, string collection, string selection, string previewPath)
    {
        var instance = Instance("test-key") with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var request = System.Text.Json.JsonDocument.Parse(body);
            Assert.Contains($"{collection} {{ {selection} }}", request.RootElement.GetProperty("query").GetString());
            Assert.Equal(5, request.RootElement.GetProperty("variables").GetProperty("filter").GetProperty("per_page").GetInt32());
            var thumbnail = new Uri(instance.WebUiUrl, previewPath).AbsoluteUri + "?apikey=private";
            var item = new { id = "42", name = "A name", title = "A title", image_path = thumbnail, front_image_path = thumbnail, paths = new { cover = thumbnail } };
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
    }

    [Theory]
    [InlineData("A marker", "A marker")]
    [InlineData("", "Primary tag")]
    public async Task SearchAsync_MarkersLinkToSceneTimestampWithoutInventingPreview(string title, string expectedTitle)
    {
        var instance = Instance(null) with { WebUiUrl = new Uri("https://stash.example/prefix/") };
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            Assert.Contains("scene_markers { id title seconds scene { id } primary_tag { name } screenshot }", body);
            return Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new { findSceneMarkers = new { scene_markers = new[] { new { id = "42", title, seconds = 123.75, scene = new { id = "9" }, primary_tag = new { name = "Primary tag" }, screenshot = "https://evil.example/thumbnail" } } } }
            }));
        })));
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var result = Assert.Single(await client.SearchAsync(instance, StashSearchKind.Markers, "query"));
            Assert.Equal(expectedTitle, result.Title);
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