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
    public async Task SearchAsync_RequestsAndClampsSelectedLimit(StashSearchKind kind, string field, string collection, int limit)
    {
        var client = new StashGraphQlClient(new HttpClient(new StubHandler((_, body) =>
        {
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            var filter = payload.RootElement.GetProperty("variables").GetProperty("filter");
            Assert.Equal(limit, filter.GetProperty("per_page").GetInt32());
            Assert.Equal(1, filter.GetProperty("page").GetInt32());
            var items = Enumerable.Range(1, 30).Select(index => new { id = index.ToString(), title = "Title", name = "Name" });
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