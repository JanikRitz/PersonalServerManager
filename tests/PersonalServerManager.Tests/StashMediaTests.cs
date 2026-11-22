using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class StashMediaTests
{
    private static readonly Guid InstanceId = Guid.Parse("0b8f3f8f-6a3c-4c1e-8f0e-5b7a3e2d9c41");

    [Theory]
    [InlineData("images", "image/12/image", "image/jpeg")]
    [InlineData("images", "image/12/image", "video/mp4")]
    [InlineData("scenes", "scene/12/stream", "video/mp4")]
    public async Task ServeAsync_UsesFixedPathAndServerSideBearer(string kind, string path, string mime)
    {
        var context = await ServeAsync(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://stash.example/prefix/" + path, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            return Media(HttpStatusCode.OK, mime, [1, 2, 3]);
        }, kind);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(mime, context.Response.ContentType);
        Assert.Equal(3, context.Response.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal("default-src 'none'; media-src 'self'; img-src 'self'; frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy);
        Assert.False(context.Response.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("../scenes", 1)]
    [InlineData("images/../scene", 1)]
    [InlineData("Images", 1)]
    [InlineData("images", 0)]
    [InlineData("scenes", -1)]
    public async Task ServeAsync_RejectsInvalidPaths(string kind, long id)
    {
        var context = await ServeAsync(_ => throw new InvalidOperationException("Must not send."), kind, id);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task ServeAsync_RejectsUnknownInstance()
    {
        var context = await ServeAsync(_ => throw new InvalidOperationException("Must not send."), instanceId: Guid.NewGuid());
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task ServeAsync_ForwardsRangeAndPartialResponse()
    {
        var context = await ServeAsync(request =>
        {
            Assert.Equal("bytes=1-2", request.Headers.Range!.ToString());
            var response = Media(HttpStatusCode.PartialContent, "video/mp4", [2, 3]);
            response.Content.Headers.ContentRange = new(1, 2, 3);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }, "scenes", range: "bytes=1-2");
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 1-2/3", context.Response.Headers.ContentRange);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal(new byte[] { 2, 3 }, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("bytes=0-1,3-4")]
    [InlineData("items=0-1")]
    [InlineData("not-a-range")]
    public async Task ServeAsync_DoesNotForwardInvalidRanges(string range)
    {
        var context = await ServeAsync(request =>
        {
            Assert.Null(request.Headers.Range);
            return Media(HttpStatusCode.OK, "image/png", [1]);
        }, range: range);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ServeAsync_PassesUnsatisfiableRange()
    {
        var context = await ServeAsync(_ =>
        {
            var response = Media(HttpStatusCode.RequestedRangeNotSatisfiable, "image/png", []);
            response.Content.Headers.ContentRange = new(3);
            return response;
        }, range: "bytes=9-10");
        Assert.Equal(416, context.Response.StatusCode);
        Assert.Equal("bytes */3", context.Response.Headers.ContentRange);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html", "images", 502)]
    [InlineData(HttpStatusCode.OK, "image/svg+xml", "images", 502)]
    [InlineData(HttpStatusCode.OK, "image/png", "scenes", 502)]
    [InlineData(HttpStatusCode.PartialContent, "image/png", "images", 502)]
    [InlineData(HttpStatusCode.Found, "image/png", "images", 502)]
    [InlineData(HttpStatusCode.NotFound, "image/png", "images", 404)]
    [InlineData(HttpStatusCode.Unauthorized, "image/png", "images", 502)]
    public async Task ServeAsync_RejectsUnexpectedResponses(HttpStatusCode status, string mime, string kind, int expected)
    {
        var context = await ServeAsync(_ =>
        {
            var response = Media(status, mime, [7]);
            response.Headers.Location = new Uri("https://other.example/private");
            return response;
        }, kind);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task ServeAsync_StreamsWithoutBuffering()
    {
        var content = new StreamingContent();
        var context = await ServeAsync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(content.WasStreamed);
        Assert.Null(context.Response.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task ServeAsync_RejectsPartialResponseWithoutContentRange()
    {
        var context = await ServeAsync(_ => Media(HttpStatusCode.PartialContent, "image/png", [1]), range: "bytes=0-0");
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task ServeAsync_MapsTransportFailureToBadGateway()
    {
        var context = await ServeAsync(_ => throw new HttpRequestException("private"));
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task Endpoint_RequiresAuthenticationBeforeContactingStash()
    {
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new StashMedia(new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return Media(HttpStatusCode.OK, "image/png", [1, 2, 3]);
        })), new Store()));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStashMediaEndpoints();
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var client = new HttpClient { BaseAddress = address };
        var path = $"/media/stash/{InstanceId:D}/images/12";
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, requests);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Authentication", "yes");
        using var authenticated = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await authenticated.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public void RouteRequiresAuthorizationAndHandlerDisablesRedirectsAndCookies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddHttpClient<StashMedia>();
        using var app = builder.Build();
        app.MapStashMediaEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        using var handler = Assert.IsType<SocketsHttpHandler>(StashMedia.CreateHttpHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Theory]
    [InlineData("image-previews", 12, null, "image/12/preview", "image/webp")]
    [InlineData("scene-previews", 12, null, "scene/12/preview", "video/mp4")]
    [InlineData("scene-webp", 12, null, "scene/12/webp", "image/webp")]
    [InlineData("markers", 42, 9L, "scene/9/scene_marker/42/stream", "video/mp4")]
    [InlineData("marker-previews", 42, 9L, "scene/9/scene_marker/42/preview", "image/webp")]
    public async Task ServesPreviewAndMarkerEndpoints(string kind, long id, long? sceneId, string expectedPath, string mime)
    {
        Uri? requested = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return Media(HttpStatusCode.OK, mime, [1, 2, 3]);
        }));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new StashMedia(client, new Store()).ServeAsync(InstanceId, kind, id, sceneId, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(mime, context.Response.ContentType);
        Assert.Equal($"https://stash.example/prefix/{expectedPath}", requested?.AbsoluteUri);
    }

    private static async Task<DefaultHttpContext> ServeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string kind = "images", long id = 12, string? range = null, Guid? instanceId = null)
    {
        using var client = new HttpClient(new StubHandler(respond));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (range is not null) context.Request.Headers.Range = range;
        await new StashMedia(client, new Store()).ServeAsync(instanceId ?? InstanceId, kind, id, context);
        return context;
    }

    private static HttpResponseMessage Media(HttpStatusCode status, string mime, byte[] bytes) => new(status)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(mime) } }
    };

    private sealed class Store : IStashInstanceStore
    {
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StashInstance>>([new(InstanceId, "Stash", new Uri("https://stash.example/graphql"), new Uri("https://stash.example/prefix"), "test-key")]);
        public Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Authentication")) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class StreamingContent : HttpContent
    {
        public bool WasStreamed { get; private set; }
        public StreamingContent() => Headers.ContentType = new("image/png");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasStreamed = true;
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
        }
    }
}