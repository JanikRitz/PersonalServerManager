using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class PreviewServiceTests
{
    [Theory]
    [InlineData("video/mp4", 206)]
    [InlineData("text/html", 502)]
    [InlineData("image/svg+xml", 502)]
    public async Task HydrusMedia_StreamsOnlyBrowserSafeTypesAndForwardsSingleRange(string mime, int expectedStatus)
    {
        var id = Guid.NewGuid();
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://configured.example/prefix/get_files/file?file_id=42", request.RequestUri!.AbsoluteUri);
            Assert.Equal("secret", request.Headers.GetValues("Hydrus-Client-API-Access-Key").Single());
            Assert.Equal("bytes=1-2", request.Headers.Range!.ToString());
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([2, 3])
                {
                    Headers = { ContentType = new(mime), ContentRange = new(1, 2, 3) }
                }
            };
        }));
        var media = new HydrusClientMedia(client, new HydrusStore(id));
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=1-2";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await media.ServeAsync(id, 42, context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        if (expectedStatus == 206)
        {
            Assert.Equal("bytes 1-2/3", context.Response.Headers.ContentRange);
            Assert.Equal(new byte[] { 2, 3 }, body.ToArray());
        }
        else Assert.Empty(body.ToArray());
    }

    [Theory]
    [InlineData("stash", "scenes", "scene/42/screenshot")]
    [InlineData("stash", "images", "image/42/thumbnail")]
    [InlineData("stash", "performers", "performer/42/image")]
    [InlineData("stash", "studios", "studio/42/image")]
    [InlineData("stash", "tags", "tag/42/image")]
    [InlineData("stash", "galleries", "gallery/42/cover")]
    [InlineData("stash", "groups", "group/42/frontimage")]
    [InlineData("hydrus", "files", "get_files/thumbnail?file_id=42")]
    public async Task FetchAsync_UsesConfiguredInstanceAndCredential(string provider, string category, string expectedPath)
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://configured.example/prefix/" + expectedPath, request.RequestUri!.AbsoluteUri);
            Assert.Equal(provider == "stash" ? "Bearer secret" : null, request.Headers.Authorization?.ToString());
            Assert.Equal(provider == "hydrus" ? "secret" : null,
                request.Headers.TryGetValues("Hydrus-Client-API-Access-Key", out var values) ? values.Single() : null);
            return Image();
        });
        var service = Service(handler, id);

        Assert.Equal($"/previews/{provider}/{id:D}/{category}/42", PreviewService.CreateLink(provider, id, category, "42")?.OriginalString);
        var result = await service.FetchAsync(provider, id, category, "42", CancellationToken.None);

        Assert.Equal("image/png", result!.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Content);
    }

    [Theory]
    [InlineData("stash", "scenes", "../42")]
    [InlineData("stash", "images", "42?url=https://evil.example")]
    [InlineData("stash", "files", "42")]
    [InlineData("stash", "markers", "42/9")]
    [InlineData("hydrus", "tags", "42")]
    [InlineData("other", "files", "42")]
    [InlineData("hydrus", "files", "0")]
    public async Task FetchAsync_RejectsInvalidRoutesWithoutSending(string provider, string category, string id)
    {
        var instanceId = Guid.NewGuid();
        var service = Service(new StubHandler(_ => throw new Exception("Unexpected request")), instanceId);
        Assert.Null(PreviewService.CreateLink(provider, instanceId, category, id));
        Assert.Null(await service.FetchAsync(provider, instanceId, category, id, CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_MarkersResolveSceneAndFetchOnlyCanonicalScreenshot()
    {
        var id = Guid.NewGuid();
        var requests = 0;
        var service = Service(new StubHandler(request =>
        {
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            requests++;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("https://configured.example/prefix/graphql", request.RequestUri!.AbsoluteUri);
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("findSceneMarkers(ids: [$id], filter: { per_page: 1, page: 1 }) { scene_markers { id scene { id } } }", body);
                Assert.Contains("\"id\":\"42\"", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"42\",\"scene\":{\"id\":\"9\"}}]}}}", System.Text.Encoding.UTF8, "application/json")
                };
            }
            Assert.Equal("https://configured.example/prefix/scene/9/scene_marker/42/screenshot", request.RequestUri!.AbsoluteUri);
            return Image();
        }), id);

        Assert.Equal($"/previews/stash/{id:D}/markers/42", PreviewService.CreateLink("stash", id, "Markers", "42")?.OriginalString);
        var image = await service.FetchAsync("stash", id, "markers", "42", CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, image!.Content);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("image/svg+xml", 3)]
    [InlineData("text/html", 3)]
    [InlineData("image/png", 5242881)]
    public async Task FetchAsync_RejectsUnsafeOrOversizedResponses(string contentType, int length)
    {
        var id = Guid.NewGuid();
        var service = Service(new StubHandler(_ =>
        {
            var response = Image();
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            response.Content.Headers.ContentLength = length;
            return response;
        }), id);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("stash", id, "images", "42", CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[]}}}", false)]
    [InlineData("{\"errors\":[{\"message\":\"Failed\"}]}", true)]
    [InlineData("not json", true)]
    [InlineData("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"42\",\"scene\":{\"id\":\"https://evil.example\"}}]}}}", true)]
    public async Task FetchAsync_MissingOrInvalidMarkerNeverFallsBackToSceneCover(string payload, bool throws)
    {
        var id = Guid.NewGuid();
        var requests = 0;
        var service = Service(new StubHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };
        }), id);
        if (throws)
            await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("stash", id, "markers", "42", CancellationToken.None));
        else
            Assert.Null(await service.FetchAsync("stash", id, "markers", "42", CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/avif")]
    public async Task FetchAsync_MarkerSupportsImageMimeTypesWithoutApiKey(string contentType)
    {
        var id = Guid.NewGuid();
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Post) return MarkerResolution();
            var response = Image();
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        }));
        var instance = new StashInstance(id, "Stash", new Uri("https://configured.example/graphql"), new Uri("https://configured.example/"), null);
        var service = new PreviewService(client, new FixedStashStore(instance), new HydrusStore(id));
        var image = await service.FetchAsync("stash", id, "markers", "42", CancellationToken.None);
        Assert.Equal(contentType, image!.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Content);
    }

    [Theory]
    [InlineData("text/html", 3, 200)]
    [InlineData("image/svg+xml", 3, 200)]
    [InlineData("image/jpeg", 5242881, 200)]
    [InlineData("image/jpeg", 3, 404)]
    [InlineData("image/jpeg", 3, 302)]
    public async Task FetchAsync_MarkerRejectsUnsafeOrFailedImage(string contentType, int length, int status)
    {
        var id = Guid.NewGuid();
        var service = Service(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post) return MarkerResolution();
            var response = Image();
            response.StatusCode = (HttpStatusCode)status;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            response.Content.Headers.ContentLength = length;
            return response;
        }), id);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("stash", id, "markers", "42", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_MarkerPropagatesCancellationDuringBodyRead()
    {
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new StubHandler(request => request.Method == HttpMethod.Post ? MarkerResolution() : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream(started))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
            }
        }), id);
        var fetch = service.FetchAsync("stash", id, "markers", "42", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private static HttpResponseMessage MarkerResolution() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"data\":{\"findSceneMarkers\":{\"scene_markers\":[{\"id\":\"42\",\"scene\":{\"id\":\"9\"}}]}}}", System.Text.Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task FetchAsync_RejectsOversizedStreamingResponseWithoutContentLength()
    {
        var id = Guid.NewGuid();
        var service = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[5 * 1024 * 1024 + 1]))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        }), id);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.FetchAsync("stash", id, "images", "42", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_PropagatesCancellationDuringBodyRead()
    {
        var id = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream(started))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        }), id);
        var fetch = service.FetchAsync("stash", id, "images", "42", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task FetchAsync_TimesOutDuringBodyRead()
    {
        var id = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream(started))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        }), id);
        var fetch = service.FetchAsync("stash", id, "images", "42", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public async Task FetchAsync_DoesNotRequestUnknownInstance()
    {
        var service = Service(new StubHandler(_ => throw new Exception("Unexpected request")), Guid.NewGuid());
        Assert.Null(await service.FetchAsync("stash", Guid.NewGuid(), "images", "42", CancellationToken.None));
    }

    [Fact]
    public async Task PreviewEndpoint_RequiresAuthenticationAndReturnsPrivateImage()
    {
        var id = Guid.NewGuid();
        var requests = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Service(new StubHandler(_ => { Interlocked.Increment(ref requests); return Image(); }), id));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPreviewEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = Address(app) };

        var path = $"/previews/stash/{id}/images/42";
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, requests);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Authentication", "yes");
        using var authenticated = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal("image/png", authenticated.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", authenticated.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", authenticated.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(new byte[] { 1, 2, 3 }, await authenticated.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("markers")]
    public async Task FetchAsync_DoesNotFollowRedirectsOrForwardCredentials(string category)
    {
        var redirectsFollowed = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/graphql", () => Results.Redirect("/capture"));
        app.MapGet("/image/42/thumbnail", () => Results.Redirect("/capture"));
        app.MapGet("/capture", () => { Interlocked.Increment(ref redirectsFollowed); return Results.Ok(); });
        await app.StartAsync();
        var id = Guid.NewGuid();
        var address = Address(app);
        var stash = new FixedStashStore(new StashInstance(id, "Stash", new Uri(address, "graphql"), address, "secret"));
        using var client = new HttpClient(PreviewService.CreateHttpHandler());
        var service = new PreviewService(client, stash, new HydrusStore(id));

        var exception = await Record.ExceptionAsync(() => service.FetchAsync("stash", id, category, "42", CancellationToken.None));
        Assert.True(exception is InvalidDataException or HttpRequestException);
        Assert.Equal(0, redirectsFollowed);
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static PreviewService Service(HttpMessageHandler handler, Guid id) => new(
        new HttpClient(handler), new StashStore(id), new HydrusStore(id));

    private static HttpResponseMessage Image() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") } }
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class BlockingStream(TaskCompletionSource started) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _stream = new(content);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _stream.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
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

    private sealed class FixedStashStore(StashInstance instance) : IStashInstanceStore
    {
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StashInstance>>([instance]);
        public Task SaveAsync(StashInstance value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StashStore(Guid id) : IStashInstanceStore
    {
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StashInstance>>([new(id, "Stash", new Uri("https://configured.example/prefix/graphql"), new Uri("https://configured.example/prefix/"), "secret")]);
        public Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class HydrusStore(Guid id) : IHydrusInstanceStore
    {
        public Task<IReadOnlyList<HydrusInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HydrusInstance>>([new(id, "Hydrus", new Uri("https://configured.example/prefix/"), "secret")]);
        public Task SaveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}