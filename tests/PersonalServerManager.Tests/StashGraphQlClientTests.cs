using System.Net;
using System.Text;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class StashGraphQlClientTests
{
    [Fact]
    public async Task SearchAsync_MapsResultsAndUsesInstanceAuthentication()
    {
        var handler = new StubHandler((request, body) =>
        {
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            Assert.Contains("findScenes", body);
            Assert.Contains("per_page", body);
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