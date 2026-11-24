using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PersonalServerManager.Covers;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class CoverPromptServiceTests
{
    [Fact]
    public async Task Settings_DefaultRoundTripAndInvalidFileIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var service = new CoverPromptService(path);
            Assert.Equal(new CoverPromptSettings(), await service.GetSettingsAsync());
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "{\"baseUrl\":\"http://localhost:1234/v1\",\"model\":\"model\"}");
            Assert.Equal(new CoverPromptSettings("http://localhost:1234/v1", "model"), await service.GetSettingsAsync());
            await service.SaveSettingsAsync(new CoverPromptSettings("http://localhost:1234/v1/", " model ", "Describe a watercolor cover.\nNo text."));
            Assert.Equal(new CoverPromptSettings("http://localhost:1234/v1", "model", "Describe a watercolor cover.\nNo text."), await new CoverPromptService(path).GetSettingsAsync());
            await File.WriteAllTextAsync(path, "not json");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetSettingsAsync());
            Assert.Equal("not json", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("file:///tmp/test")]
    [InlineData("http://user:secret@localhost/v1")]
    [InlineData("http://localhost/v1?key=secret")]
    [InlineData("http://localhost/v1#fragment")]
    [InlineData("not a url")]
    public async Task RejectsInvalidUrls(string url)
    {
        var service = new CoverPromptService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSettingsAsync(new CoverPromptSettings(url, "model")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new CoverPromptSettings(url, "model"), "title", null, null));
    }

    [Fact]
    public async Task Generate_SendsChatCompletionAndReturnsContent()
    {
        using var client = new HttpClient(new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://localhost:8080/prefix/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
            var messages = payload.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("Describe a watercolor cover.\nNo text.", messages[0].GetProperty("content").GetString());
            var user = messages[1].GetProperty("content").GetString();
            Assert.Contains("Title: Book", user);
            Assert.Contains("Author: Writer", user);
            Assert.Contains("Description: Story", user);
            return Json("{\"choices\":[{\"message\":{\"content\":\"  A striking cover  \"}}]}");
        }));
        var service = new CoverPromptService("unused.json", client);
        Assert.Equal("A striking cover", await service.GenerateAsync(new("http://localhost:8080/prefix/v1/", "test-model", "Describe a watercolor cover.\nNo text."), "Book", "Writer", "Story"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    [InlineData("invalid json")]
    public async Task Generate_RejectsInvalidResponses(string body)
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(Json(body))));
        var service = new CoverPromptService("unused.json", client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null));
    }

    [Fact]
    public async Task Generate_RejectsLargeResponseAndSafeHttpErrors()
    {
        using var largeClient = new HttpClient(new StubHandler(_ => Task.FromResult(Json(new string('x', 300000)))));
        var service = new CoverPromptService("unused.json", largeClient);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null));
        using var errorClient = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret") })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new CoverPromptService("unused.json", errorClient).GenerateAsync(new(Model: "model"), "title", null, null));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Generate_RejectsRedirectAndHonorsCancellation()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))));
        var service = new CoverPromptService("unused.json", client);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null, canceled.Token));
    }

    [Fact]
    public async Task Generate_DefaultClientDoesNotFollowRedirects()
    {
        var source = new TcpListener(IPAddress.Loopback, 0);
        var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        try
        {
            var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var server = Task.Run(async () =>
            {
                using var connection = await source.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: timeout.Token);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
            });
            var service = new CoverPromptService("unused.json");
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.GenerateAsync(
                new($"http://127.0.0.1:{sourcePort}/v1", "model"), "title", null, null, timeout.Token));
            await server;
            Assert.Contains("redirect", error.Message);
            Assert.False(target.Pending());
        }
        finally { source.Stop(); target.Stop(); }
    }

    [Fact]
    public async Task Generate_RejectsUnknownLengthOversizedResponse()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent()
        })));
        var service = new CoverPromptService("unused.json", client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null));
    }

    [Fact]
    public async Task Generate_ValidatesInputsBeforeSending()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Must not send.")));
        var service = new CoverPromptService("unused.json", client);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model"), " ", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model"), "title", new string('a', 1025), null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model"), "title", null, new string('d', 32001)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model", Instruction: " "), "title", null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model", Instruction: new string('x', 8001)), "title", null, null));
    }

    [Fact]
    public async Task Generate_UsesFirstAdvertisedModelWhenUnspecified()
    {
        using var client = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("{\"data\":[{\"id\":\"available\"}]}");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("available", payload.RootElement.GetProperty("model").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"cover\"}}]}");
        }));
        Assert.Equal("cover", await new CoverPromptService("unused.json", client).GenerateAsync(new(), "Book", null, null));
    }

    [Fact]
    public async Task Generate_DistinguishesCallerCancellationAndClientTimeout()
    {
        using var client = new HttpClient(new BlockingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new CoverPromptService("unused.json", client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null, cancellation.Token));
        await Assert.ThrowsAsync<TimeoutException>(() => service.GenerateAsync(new(Model: "model"), "title", null, null));
    }

    [Fact]
    public async Task Generate_IncludesExcerptAndSendsImageAsVisionContent()
    {
        using var client = new HttpClient(new StubHandler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var content = payload.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            var text = content[0].GetProperty("text").GetString();
            Assert.Contains("Opening text:\nIt was a dark night.", text);
            Assert.Contains("first page of the comic", text);
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,iVBORw==", content[1].GetProperty("image_url").GetProperty("url").GetString());
            return Json("{\"choices\":[{\"message\":{\"content\":\"cover\"}}]}");
        }));
        var service = new CoverPromptService("unused.json", client);
        var image = new PromptImage([0x89, (byte)'P', (byte)'N', (byte)'G'], "image/png");
        Assert.Equal("cover", await service.GenerateAsync(new(Model: "model"), "Book", null, null, excerpt: "It was a dark night.", image: image));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model"), "Book", null, null, image: image with { ContentType = "image/jpeg" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(new(Model: "model"), "Book", null, null, excerpt: new string('e', 32001)));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[300000]).AsTask();
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}