using System.Net;
using System.Text;
using System.Text.Json;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class SwarmUICoverGeneratorTests
{
    private static readonly SwarmUIInstance Instance = new(Guid.NewGuid(), "test", new Uri("http://swarm.example/prefix"), []);

    [Fact]
    public async Task GenerateAsync_SendsFlatParametersAndFetchesImage()
    {
        var calls = 0;
        using var generator = new SwarmUICoverGenerator(new Handler(async request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")) return Json("{\"session_id\":\"session\"}");
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/prefix/View/local/raw/a%20b.png", request.RequestUri.AbsolutePath);
                return Image([1, 2, 3], "image/png");
            }
            var content = await request.Content!.ReadAsStringAsync();
            Assert.Equal(Encoding.UTF8.GetByteCount(content), request.Content.Headers.ContentLength);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            Assert.Equal("session", root.GetProperty("session_id").GetString());
            Assert.Equal(1, root.GetProperty("images").GetInt32());
            Assert.Equal("cover", root.GetProperty("prompt").GetString());
            Assert.Equal("model", root.GetProperty("model").GetString());
            Assert.Equal(20, root.GetProperty("steps").GetInt32());
            Assert.Equal(3.5, root.GetProperty("cfgscale").GetDouble());
            Assert.Equal(512, root.GetProperty("width").GetInt32());
            Assert.Equal(768, root.GetProperty("height").GetInt32());
            Assert.False(root.TryGetProperty("parameters", out _));
            return Json("{\"images\":[\"View/local/raw/a%20b.png\"]}");
        }));

        var result = await generator.GenerateAsync(Instance, "cover", "model", 20, 3.5, 512, 768);

        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.OK)]
    public async Task GenerateAsync_RenewsInvalidSessionOnce(HttpStatusCode status)
    {
        var sessions = 0;
        var generations = 0;
        using var generator = new SwarmUICoverGenerator(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession"))
                return Json($$"""{"session_id":"s{{++sessions}}"}""");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal($"s{++generations}", document.RootElement.GetProperty("session_id").GetString());
            return generations == 1 ? Json("{\"error_id\":\"invalid_session_id\"}", status)
                : Json("{\"images\":[\"data:image/jpg;base64,AQID\"]}");
        }));

        var result = await generator.GenerateAsync(Instance, "cover", "model", 20, 3.5, 512, 768);

        Assert.Equal(2, sessions);
        Assert.Equal(2, generations);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("image/jpeg", result.ContentType);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotRetryInvalidSessionMoreThanOnce()
    {
        var sessions = 0;
        var generations = 0;
        using var generator = new SwarmUICoverGenerator(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
                ? Json($$"""{"session_id":"s{{++sessions}}"}""")
                : Json($$"""{"error_id":"invalid_session_id","count":{{++generations}}}""", HttpStatusCode.Unauthorized))));

        await Assert.ThrowsAsync<HttpRequestException>(() => Generate(generator));
        Assert.Equal(2, sessions);
        Assert.Equal(2, generations);
    }

    [Theory]
    [InlineData("https://evil.example/View/local/raw/a.png")]
    [InlineData("//evil.example/View/local/raw/a.png")]
    [InlineData("View/local/../secret.png")]
    [InlineData("View/local/%2e%2e/secret.png")]
    [InlineData("View/local/%252e%252e/secret.png")]
    [InlineData("View/local/raw/a.png?x=1")]
    [InlineData("View/local/raw/a.png#x")]
    [InlineData("View/local/raw\\a.png")]
    [InlineData("View/local/raw/video.mp4")]
    [InlineData("data:image/svg+xml;base64,AQID")]
    [InlineData("data:image/png;base64,%%%")]
    [InlineData("data:image/png,abc")]
    [InlineData("data:image/png;base64,")]
    public async Task GenerateAsync_RejectsUnsafeImageLocations(string location)
    {
        var requests = 0;
        using var generator = new SwarmUICoverGenerator(new Handler(request =>
        {
            requests++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
                ? Json("{\"session_id\":\"s\"}")
                : Json(JsonSerializer.Serialize(new { images = new[] { location } })));
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => Generate(generator));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"images\":[]}")]
    [InlineData("{\"images\":[123]}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task GenerateAsync_RejectsMalformedOrErrorResponses(string payload)
    {
        using var generator = GeneratorWithResponse(payload);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Generate(generator));
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task GenerateAsync_ReportsSwarmGenerationError(HttpStatusCode status)
    {
        const string message = "ComfyUI execution error (Node 10: SwarmKSampler): module 'comfy.model_management' has no attribute 'directml_enabled'";
        using var generator = new SwarmUICoverGenerator(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession") ? Json("{\"session_id\":\"s\"}")
                : Json(JsonSerializer.Serialize(new { error = message }), status))));

        var exception = await Assert.ThrowsAsync<SwarmUIGenerationException>(() => Generate(generator));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_LimitsAndSanitizesSwarmGenerationError()
    {
        using var generator = GeneratorWithResponse(JsonSerializer.Serialize(new { error = "failure\0" + new string('x', 3000) }));
        var exception = await Assert.ThrowsAsync<SwarmUIGenerationException>(() => Generate(generator));
        Assert.Equal(2000, exception.Message.Length);
        Assert.DoesNotContain('\0', exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_RejectsRedirectAndNonImageResponse()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://evil.example/") } },
            Image([1], "text/html")
        })
        {
            using var generator = new SwarmUICoverGenerator(new Handler(request => Task.FromResult(
                request.Method == HttpMethod.Get ? response : request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
                    ? Json("{\"session_id\":\"s\"}") : Json("{\"images\":[\"/View/local/raw/a.png\"]}"))));
            if (response.StatusCode == HttpStatusCode.Redirect)
                await Assert.ThrowsAsync<HttpRequestException>(() => Generate(generator));
            else
                await Assert.ThrowsAsync<InvalidDataException>(() => Generate(generator));
        }
    }

    [Fact]
    public void Constructor_DisablesRedirectsAndCookiesOnHttpHandlers()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true };
        using var generator = new SwarmUICoverGenerator(handler);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task GenerateAsync_RejectsOversizedJsonAndImage()
    {
        using var oversizedJson = new SwarmUICoverGenerator(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession") ? Json("{\"session_id\":\"s\"}")
                : Json(new string('x', 48 * 1024 * 1024 + 1)))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Generate(oversizedJson));

        using var oversizedImage = new SwarmUICoverGenerator(new Handler(request => Task.FromResult(
            request.Method == HttpMethod.Get ? Image(new byte[32 * 1024 * 1024 + 1], "image/png")
                : request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession") ? Json("{\"session_id\":\"s\"}")
                : Json("{\"images\":[\"View/local/raw/a.png\"]}"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Generate(oversizedImage));
    }

    [Fact]
    public async Task GenerateAsync_RejectsOversizedImageWithoutContentLength()
    {
        using var generator = new SwarmUICoverGenerator(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get)
                return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
                    ? Json("{\"session_id\":\"s\"}") : Json("{\"images\":[\"View/local/raw/a.png\"]}"));
            var content = new StreamContent(new NonSeekableStream(new byte[32 * 1024 * 1024 + 1]));
            content.Headers.ContentType = new("image/png");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => Generate(generator));
    }

    [Fact]
    public async Task GenerateAsync_RespectsCancellationAndTimeout()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var generator = new SwarmUICoverGenerator(new WaitingHandler(), TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(Instance, "cover", "model", 20, 3.5, 512, 768, canceled.Token));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Generate(generator));
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_RejectsInvalidInputsBeforeSending()
    {
        using var generator = new SwarmUICoverGenerator(new Handler(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(Instance, " ", "model", 20, 3.5, 512, 768));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(Instance, "cover", " ", 20, 3.5, 512, 768));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.GenerateAsync(Instance, "cover", "model", 0, 3.5, 512, 768));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.GenerateAsync(Instance, "cover", "model", 20, double.NaN, 512, 768));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.GenerateAsync(Instance, "cover", "model", 20, 3.5, 0, 768));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.GenerateAsync(Instance, "cover", "model", 20, 3.5, 512, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => Generate(generator, Instance with { BaseUrl = new Uri("http://user:pass@swarm.example/") }));
    }

    [Fact]
    public async Task UpscaleAsync_SendsSourceAsInitImageAtTargetSize()
    {
        using var generator = new SwarmUICoverGenerator(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")) return Json("{\"session_id\":\"session\"}");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal("data:image/png;base64,AQID", root.GetProperty("initimage").GetString());
            Assert.Equal(0.3, root.GetProperty("initimagecreativity").GetDouble());
            Assert.Equal(1024, root.GetProperty("width").GetInt32());
            Assert.Equal(1536, root.GetProperty("height").GetInt32());
            Assert.Equal("cover", root.GetProperty("prompt").GetString());
            return Json("{\"images\":[\"data:image/png;base64,BAUG\"]}");
        }));

        var result = await generator.UpscaleAsync(Instance, new GeneratedCover([1, 2, 3], "image/png"), "cover", "model", 20, 3.5, 1024, 1536);

        Assert.Equal(new byte[] { 4, 5, 6 }, result.Bytes);
        await Assert.ThrowsAsync<ArgumentException>(() => generator.UpscaleAsync(Instance, new GeneratedCover([], "image/png"), "cover", "model", 20, 3.5, 1024, 1536));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.UpscaleAsync(Instance, new GeneratedCover([1], "text/html"), "cover", "model", 20, 3.5, 1024, 1536));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => generator.UpscaleAsync(Instance, new GeneratedCover([1], "image/png"), "cover", "model", 20, 3.5, 1024, 1536, 1.5));
    }

    private static Task<GeneratedCover> Generate(SwarmUICoverGenerator generator, SwarmUIInstance? instance = null) =>
        generator.GenerateAsync(instance ?? Instance, "cover", "model", 20, 3.5, 512, 768);

    private static SwarmUICoverGenerator GeneratorWithResponse(string response) => new(new Handler(request => Task.FromResult(
        request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession") ? Json("{\"session_id\":\"s\"}") : Json(response))));

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}