using System.Net;
using Microsoft.AspNetCore.Http;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class SwarmUIMediaTests
{
    private static readonly Guid InstanceId = Guid.Parse("0b8f3f8f-6a3c-4c1e-8f0e-5b7a3e2d9c41");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("raw/../Data/Settings.png")]
    [InlineData("raw/./a.png")]
    [InlineData("/raw/a.png")]
    [InlineData("raw\\a.png")]
    [InlineData("other/a.png")]
    [InlineData("rawx/a.png")]
    [InlineData("raw/a.txt")]
    [InlineData("raw/a.png?x=1")]
    public async Task ServeAsync_RejectsPathsOutsideSearchPathsWithoutCallingUpstream(string? path)
    {
        var (status, _, _) = await ServeAsync(_ => throw new InvalidOperationException("Must not send."), path, false);
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ServeAsync_RejectsUnknownInstance()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Must not send.")));
        var context = Context();
        await new SwarmUIMedia(client, new Store()).ServeAsync(Guid.NewGuid(), "raw/a.png", false, context);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task ServeAsync_RequestsPreviewWithoutRangeAndNormalizesJpegType()
    {
        var (status, context, body) = await ServeAsync(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://swarm.example/prefix/View/local/raw/2026-08-30/1236001-90s%20TV%2C%20grainy.mp4?preview=true",
                request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Range);
            return Media(HttpStatusCode.OK, "image/jpg", [1, 2, 3]);
        }, "raw/2026-08-30/1236001-90s TV, grainy.mp4", true, "bytes=0-1");

        Assert.Equal(200, status);
        Assert.Equal("image/jpeg", context.Response.ContentType);
        Assert.Equal(3, context.Response.ContentLength);
        Assert.Equal([1, 2, 3], body);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal("default-src 'none'; media-src 'self'; img-src 'self'; frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy);
    }

    [Theory]
    [InlineData("video/mp4", false)]
    [InlineData("image/png", true)]
    public async Task ServeAsync_RejectsNonImageOrOversizedPreviews(string mime, bool oversized)
    {
        var (status, _, body) = await ServeAsync(_ => oversized
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new MemoryStream(new byte[5 * 1024 * 1024 + 1]))) { Headers = { ContentType = new(mime) } } }
            : Media(HttpStatusCode.OK, mime, [1]), "raw/a.png", true);

        Assert.Equal(502, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ServeAsync_RejectsDeclaredOversizedPreview()
    {
        var (status, _, _) = await ServeAsync(_ =>
        {
            var response = Media(HttpStatusCode.OK, "image/png", [1]);
            response.Content.Headers.ContentLength = 5 * 1024 * 1024 + 1;
            return response;
        }, "raw/a.png", true);
        Assert.Equal(502, status);
    }

    [Fact]
    public async Task ServeAsync_ForwardsSingleRangeForFullMedia()
    {
        var (status, context, body) = await ServeAsync(request =>
        {
            Assert.Equal("https://swarm.example/prefix/View/local/raw/a.mp4", request.RequestUri!.AbsoluteUri);
            Assert.Equal("bytes=1-2", request.Headers.Range!.ToString());
            var response = Media(HttpStatusCode.PartialContent, "video/mp4", [2, 3]);
            response.Content.Headers.ContentRange = new(1, 2, 3);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }, "raw/a.mp4", false, "bytes=1-2");

        Assert.Equal(206, status);
        Assert.Equal("video/mp4", context.Response.ContentType);
        Assert.Equal("bytes 1-2/3", context.Response.Headers.ContentRange);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal([2, 3], body);
    }

    [Fact]
    public async Task ServeAsync_DoesNotForwardMultipleRanges()
    {
        var (status, _, _) = await ServeAsync(request =>
        {
            Assert.Null(request.Headers.Range);
            return Media(HttpStatusCode.OK, "image/png", [1]);
        }, "raw/a.png", false, "bytes=0-1,3-4");
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task ServeAsync_PassesRangeNotSatisfiable()
    {
        var (status, context, _) = await ServeAsync(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentRange = new(3);
            return response;
        }, "raw/a.png", false, "bytes=9-10");
        Assert.Equal(416, status);
        Assert.Equal("bytes */3", context.Response.Headers.ContentRange);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html", 502)]
    [InlineData(HttpStatusCode.OK, "image/svg+xml", 502)]
    [InlineData(HttpStatusCode.PartialContent, "image/png", 502)]
    [InlineData(HttpStatusCode.Found, "image/png", 502)]
    [InlineData(HttpStatusCode.InternalServerError, "image/png", 502)]
    [InlineData(HttpStatusCode.NotFound, "image/png", 404)]
    [InlineData(HttpStatusCode.OK, "video/quicktime", 200)]
    public async Task ServeAsync_MapsUpstreamResponses(HttpStatusCode upstreamStatus, string mime, int expected)
    {
        var (status, context, body) = await ServeAsync(_ =>
        {
            var response = Media(upstreamStatus, mime, [7]);
            response.Headers.Location = new Uri("https://other.example/private");
            return response;
        }, "raw/a.mov", false);

        Assert.Equal(expected, status);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Equal(expected == 200 ? new byte[] { 7 } : [], body);
    }

    [Fact]
    public async Task ServeAsync_MapsTransportFailureToBadGateway()
    {
        var (status, _, _) = await ServeAsync(_ => throw new HttpRequestException("private"), "raw/a.png", false);
        Assert.Equal(502, status);
    }

    private static async Task<(int Status, DefaultHttpContext Context, byte[] Body)> ServeAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string? path, bool preview, string? range = null)
    {
        using var client = new HttpClient(new StubHandler(respond));
        var context = Context();
        if (range is not null) context.Request.Headers.Range = range;
        await new SwarmUIMedia(client, new Store()).ServeAsync(InstanceId, path, preview, context);
        return (context.Response.StatusCode, context, ((MemoryStream)context.Response.Body).ToArray());
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static HttpResponseMessage Media(HttpStatusCode status, string mime, byte[] bytes) => new(status)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(mime) } }
    };

    private sealed class Store : ISwarmUIInstanceStore
    {
        public Task<IReadOnlyList<SwarmUIInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SwarmUIInstance>>([new(InstanceId, "Swarm", new Uri("https://swarm.example/prefix"), SwarmUIInstance.DefaultSearchPaths)]);
        public Task SaveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
