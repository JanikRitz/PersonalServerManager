using System.Net;
using System.Text;
using System.Text.Json;
using PersonalServerManager.Search;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class SwarmUIClientTests
{
    private const string ImageSource = "2026-09-26/1117001-remove background-Qwen_Image_2-1.png";
    private const string ImageMetadata = """
        {"sui_image_params":{"prompt":"remove background","model":"Qwen_Image_2-1","seed":504678709,"steps":20,"cfgscale":3.5,
        "aspectratio":"5:8","width":1152,"height":1824,"negativeprompt":"","swarm_version":"0.9.8.3","promptimages":["x"],"refiner":false},
        "sui_extra_data":{"date":"2026-09-26","prep_time":"0.03 sec","generation_time":"21.86 sec"},"sui_models":[{"name":"m"}]}
        """;

    [Fact]
    public async Task SearchAsync_CreatesSessionOnceAndSendsReadOnlyListRequests()
    {
        var sessions = 0;
        var lists = new List<JsonElement>();
        using var client = new SwarmUIClient(new StubHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("swarm.example", request.RequestUri!.Host);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), request.Content!.Headers.ContentLength);
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/prefix/API/GetNewSession":
                    Assert.Equal("{}", body);
                    sessions++;
                    return Json("""{"session_id":"abc123","user_id":"local","version":"0.9.8.4"}""");
                case "/prefix/API/ListImages":
                    lock (lists) lists.Add(JsonDocument.Parse(body).RootElement.Clone());
                    return Json("""{"folders":[],"files":[]}""");
                default:
                    throw new InvalidOperationException("Unexpected route.");
            }
        }));
        var instance = Instance(new SwarmUISearchPath("raw"), new SwarmUISearchPath("upscaled", 5));

        Assert.Empty(await client.SearchAsync(instance, "  cat  ", 5, LibrarySort.Default));
        Assert.Empty(await client.SearchAsync(instance, "cat", 5, LibrarySort.DateOldest));

        Assert.Equal(1, sessions);
        Assert.Equal(6, lists.Count);
        Assert.All(lists, list => Assert.Equal("abc123", list.GetProperty("session_id").GetString()));
        Assert.All(lists, list => Assert.Equal("Date", list.GetProperty("sortBy").GetString()));
        var starred = lists.Where(list => list.GetProperty("path").GetString() == "Starred").ToArray();
        Assert.Equal(2, starred.Length);
        Assert.All(starred, list => Assert.Equal(6, list.GetProperty("depth").GetInt32()));
        Assert.All(starred, list => Assert.Equal(JsonValueKind.Null, list.GetProperty("filter").ValueKind));
        var raw = lists.Where(list => list.GetProperty("path").GetString() == "raw").ToArray();
        Assert.Equal(["cat", "cat"], raw.Select(list => list.GetProperty("filter").GetString()));
        Assert.Equal([3, 3], raw.Select(list => list.GetProperty("depth").GetInt32()));
        Assert.Equal([false, true], raw.Select(list => list.GetProperty("sortReverse").GetBoolean()));
        Assert.Equal(5, lists.Single(list => list.GetProperty("path").GetString() == "upscaled" &&
            !list.GetProperty("sortReverse").GetBoolean()).GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task SearchAsync_RetriesOnceWithNewSessionWhenSessionExpires()
    {
        var sessions = 0;
        using var client = new SwarmUIClient(new StubHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession"))
                return Json($$"""{"session_id":"s{{Interlocked.Increment(ref sessions)}}"}""");
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("session_id").GetString() == "s1"
                ? Json("""{"error":"Invalid session ID.","error_id":"invalid_session_id"}""", HttpStatusCode.Unauthorized)
                : Json(Files(("a.png", null)));
        }));

        var file = Assert.Single(await client.SearchAsync(Instance(), "a", 5, LibrarySort.Default));

        Assert.Equal("raw/a.png", file.Path);
        Assert.InRange(sessions, 2, 3);
    }

    [Fact]
    public async Task SearchAsync_FailsWhenRenewedSessionIsAlsoRejected()
    {
        var sessions = 0;
        var lists = 0;
        using var client = new SwarmUIClient(new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
            ? Json($$"""{"session_id":"s{{Interlocked.Increment(ref sessions)}}"}""")
            : Json($$"""{"error_id":"invalid_session_id","n":{{Interlocked.Increment(ref lists)}}}""", HttpStatusCode.Unauthorized)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Instance(), "a", 5, LibrarySort.Default));
        // The search path and the starred lookup each retry exactly once.
        Assert.Equal(4, lists);
        Assert.InRange(sessions, 2, 5);
    }

    [Fact]
    public async Task SearchAsync_JoinsRelativeSourcesAndParsesMetadata()
    {
        using var client = Client(path => path switch
        {
            "Starred" => Files(("raw/2026-08-30/1236001-video.mp4", null)),
            "raw" => Files(
                (ImageSource, ImageMetadata),
                ("2026-08-30/1236001-video.mp4", """{"sui_image_params":{"prompt":"tv","model":"minimax","textvideoframes":72}}"""),
                ("2026-08-30/0000001-missing.webp", null),
                ("2026-08-30/0000002-invalid.png", "not json"),
                ("2026-08-30/0000003-array.png", "[1]"),
                ("2026-08-30/notes.txt", null),
                ("../escape.png", null),
                ("2026-08-30/bad:name.png", null)),
            _ => throw new InvalidOperationException()
        });

        var files = await client.SearchAsync(Instance(), "x", 25, LibrarySort.TitleAscending);

        Assert.Equal(["raw/2026-08-30/0000001-missing.webp", "raw/2026-08-30/0000002-invalid.png", "raw/2026-08-30/0000003-array.png",
            "raw/" + ImageSource, "raw/2026-08-30/1236001-video.mp4"], files.Select(file => file.Path));
        var image = files.Single(file => file.Path == "raw/" + ImageSource);
        Assert.Equal(SwarmUIMediaKind.Image, image.Kind);
        Assert.Equal("remove background", image.Prompt);
        Assert.Equal("Qwen_Image_2-1", image.Model);
        Assert.Equal(1152, image.Width);
        Assert.Equal(1824, image.Height);
        Assert.False(image.IsStarred);
        Assert.Equal(Local(new DateTime(2026, 9, 26, 11, 17, 0)), image.CreatedAt);
        Assert.Equal(new KeyValuePair<string, string>[]
        {
            new("prompt", "remove background"), new("model", "Qwen_Image_2-1"), new("seed", "504678709"), new("steps", "20"),
            new("cfgscale", "3.5"), new("aspectratio", "5:8"), new("width", "1152"), new("height", "1824"),
            new("swarm_version", "0.9.8.3"), new("refiner", "false"), new("generation_time", "21.86 sec")
        }, image.Metadata);

        var video = files.Single(file => file.Path.EndsWith(".mp4"));
        Assert.Equal(SwarmUIMediaKind.Video, video.Kind);
        Assert.Equal("minimax", video.Model);
        Assert.True(video.IsStarred);
        Assert.Contains(new KeyValuePair<string, string>("textvideoframes", "72"), video.Metadata);

        foreach (var file in files.Where(file => file.Path.Contains("/0000")))
        {
            Assert.Null(file.Prompt);
            Assert.Null(file.Model);
            Assert.Null(file.Width);
            Assert.Empty(file.Metadata);
            Assert.Equal(Local(new DateTime(2026, 8, 30)), file.CreatedAt);
        }
    }

    [Fact]
    public async Task SearchAsync_TruncatesLongMetadataValues()
    {
        var prompt = new string('p', 3000);
        using var client = Client(path => path == "raw"
            ? Files(("a.png", JsonSerializer.Serialize(new { sui_image_params = new { prompt } })))
            : Files());

        var file = Assert.Single(await client.SearchAsync(Instance(), "p", 5, LibrarySort.Default));

        Assert.Equal(2000, file.Prompt!.Length);
        Assert.Equal(2000, Assert.Single(file.Metadata).Value.Length);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesFilesAcrossSearchPaths()
    {
        using var client = Client(path => path == "raw" ? Files(("a.png", null), ("a.png", null), ("b.png", null)) : Files());

        var files = await client.SearchAsync(Instance(new SwarmUISearchPath("raw"), new SwarmUISearchPath("raw", 1)),
            "a", 25, LibrarySort.TitleAscending);

        Assert.Equal(["raw/a.png", "raw/b.png"], files.Select(file => file.Path));
    }

    [Fact]
    public async Task SearchAsync_TreatsStarredFailureAsNotStarred()
    {
        using var client = Client(path => path == "Starred" ? throw new HttpRequestException("down") : Files(("a.png", null)));

        Assert.False(Assert.Single(await client.SearchAsync(Instance(), "a", 5, LibrarySort.Default)).IsStarred);
    }

    [Theory]
    [InlineData(LibrarySort.Default, "raw/2026-01-30/1200001-a.png", "raw/2026-01-26/1200001-a.png")]
    [InlineData(LibrarySort.DateNewest, "raw/2026-01-30/1200001-a.png", "raw/2026-01-26/1200001-a.png")]
    [InlineData(LibrarySort.DateOldest, "raw/2026-01-01/1200001-a.png", "raw/2026-01-05/1200001-a.png")]
    public async Task SearchAsync_SortsByDateAndLimits(LibrarySort sort, string first, string fifth)
    {
        var sources = Enumerable.Range(1, 30).Select(day => ($"2026-01-{day:00}/1200001-a.png", (string?)null))
            .Append(("undated.png", null)).ToArray();
        using var client = Client(path => path == "raw" ? Files(sources) : Files());

        var files = await client.SearchAsync(Instance(), "a", 5, sort);

        Assert.Equal(5, files.Count);
        Assert.Equal(first, files[0].Path);
        Assert.Equal(fifth, files[4].Path);
        Assert.Equal(25, (await client.SearchAsync(Instance(), "a", 25, sort)).Count);
    }

    [Fact]
    public async Task SearchAsync_LimitsEachMediaKindSeparately()
    {
        var sources = Enumerable.Range(1, 9).Select(day => ($"2026-02-{day:00}/1200001-v.mp4", (string?)null))
            .Concat(Enumerable.Range(1, 3).Select(day => ($"2026-01-{day:00}/1200001-i.png", (string?)null))).ToArray();
        using var client = Client(path => path == "raw" ? Files(sources) : Files());

        var files = await client.SearchAsync(Instance(), "a", 5, LibrarySort.DateNewest);

        Assert.Equal(5, files.Count(file => file.Kind == SwarmUIMediaKind.Video));
        Assert.Equal(3, files.Count(file => file.Kind == SwarmUIMediaKind.Image));
    }

    [Fact]
    public async Task SearchAsync_PlacesUndatedFilesLastAndSortsTitlesAndRandom()
    {
        using var client = Client(path => path == "raw"
            ? Files(("undated.png", null), ("2026-01-01/b.png", null),
                ("2026-01-02/c.png", """{"sui_image_params":{"prompt":"A prompt"}}"""))
            : Files());

        Assert.Equal("raw/undated.png", (await client.SearchAsync(Instance(), "a", 5, LibrarySort.DateNewest))[^1].Path);
        Assert.Equal("raw/undated.png", (await client.SearchAsync(Instance(), "a", 5, LibrarySort.DateOldest))[^1].Path);
        Assert.Equal(["raw/2026-01-02/c.png", "raw/2026-01-01/b.png", "raw/undated.png"],
            (await client.SearchAsync(Instance(), "a", 5, LibrarySort.TitleAscending)).Select(file => file.Path));
        Assert.Equal(["raw/undated.png", "raw/2026-01-01/b.png", "raw/2026-01-02/c.png"],
            (await client.SearchAsync(Instance(), "a", 5, LibrarySort.TitleDescending)).Select(file => file.Path));
        Assert.Equal(3, (await client.SearchAsync(Instance(), "a", 5, LibrarySort.Random)).Count);
    }

    [Theory]
    [InlineData("2026-01-02/2359001-x.png", null, 2026, 1, 2, 23, 59)]
    [InlineData("2026-01-02/9999001-x.png", null, 2026, 1, 2, 0, 0)]
    [InlineData("2026-01-02/123-x.png", null, 2026, 1, 2, 0, 0)]
    [InlineData("2026-01-02/0815.png", null, 2026, 1, 2, 0, 0)]
    [InlineData("2026-01-02/nested/0815001-x.png", null, 2026, 1, 2, 8, 15)]
    [InlineData("folder/0815001-x.png", "2025-12-31", 2025, 12, 31, 8, 15)]
    [InlineData("2026-01-02/0815001-x.png", "not-a-date", 2026, 1, 2, 8, 15)]
    public async Task SearchAsync_ParsesCreatedAt(string source, string? date, int year, int month, int day, int hour, int minute)
    {
        var metadata = date is null ? null : JsonSerializer.Serialize(new { sui_extra_data = new { date } });
        using var client = Client(path => path == "raw" ? Files((source, metadata)) : Files());

        var file = Assert.Single(await client.SearchAsync(Instance(), "x", 5, LibrarySort.Default));

        Assert.Equal(Local(new DateTime(year, month, day, hour, minute, 0)), file.CreatedAt);
    }

    [Fact]
    public async Task SearchAsync_LeavesCreatedAtEmptyWithoutDate()
    {
        using var client = Client(path => path == "raw" ? Files(("folder/1200001-x.png", null)) : Files());
        Assert.Null(Assert.Single(await client.SearchAsync(Instance(), "x", 5, LibrarySort.Default)).CreatedAt);
    }

    [Fact]
    public async Task SearchAsync_RejectsOversizedResponses()
    {
        using var declared = new SwarmUIClient(new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
            ? Json("""{"session_id":"s"}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) { Headers = { ContentLength = 32L * 1024 * 1024 + 1 } } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => declared.SearchAsync(Instance(), "a", 5, LibrarySort.Default));

        using var streamed = new SwarmUIClient(new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")
            ? Json("""{"session_id":"s"}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => streamed.SearchAsync(Instance(), "a", 5, LibrarySort.Default));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task SearchAsync_FailureStatusDoesNotEchoUpstreamText(HttpStatusCode status)
    {
        var calls = 0;
        using var client = new SwarmUIClient(new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")) return Json("""{"session_id":"s"}""");
            Interlocked.Increment(ref calls);
            var response = Json("""{"error":"private detail"}""", status);
            response.Headers.Location = new Uri("https://other.example/private");
            return response;
        }));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Instance(), "a", 5, LibrarySort.Default));

        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain("private", failure.ToString());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("""{"error":"private detail"}""")]
    [InlineData("""{"error_id":"private_detail"}""")]
    [InlineData("""{"files":null}""")]
    [InlineData("private detail")]
    [InlineData("[]")]
    public async Task SearchAsync_RejectsErrorAndMalformedPayloads(string payload)
    {
        using var client = Client(path => path == "raw" ? payload : Files());
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Instance(), "a", 5, LibrarySort.Default));
        Assert.DoesNotContain("private", failure.ToString());
    }

    [Fact]
    public async Task SearchAsync_MapsTimeoutsToRequestFailures()
    {
        using var client = new SwarmUIClient(new StubHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }), TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Instance(), "a", 5, LibrarySort.Default));
    }

    [Fact]
    public async Task SearchAsync_ValidatesInputsBeforeSending()
    {
        using var client = new SwarmUIClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send.")));
        foreach (var limit in new[] { 0, 4, 6, 26 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync(Instance(), "a", limit, LibrarySort.Default));
        foreach (var query in new[] { "", "  ", new string('a', 1025) })
            await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(Instance(), query, 5, LibrarySort.Default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync(Instance(), "a", 5, (LibrarySort)99));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(
            Instance() with { BaseUrl = new Uri("ftp://swarm.example/") }, "a", 5, LibrarySort.Default));
        Assert.Empty(await client.SearchAsync(Instance() with { SearchPaths = [] }, "a", 5, LibrarySort.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwarmUIClient(requestTimeout: TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public async Task ObserveAsync_ReportsSanitizedVersion()
    {
        using var client = new SwarmUIClient(new StubHandler((request, body) =>
        {
            Assert.Equal("/prefix/API/GetNewSession", request.RequestUri!.AbsolutePath);
            return Json($$"""{"session_id":"s","version":"0.9.8.4.GIT-abc\u0007\n{{new string('x', 100)}}"}""");
        }));

        var observation = await client.ObserveAsync(Instance());

        Assert.True(observation.IsReachable);
        Assert.Equal($"SwarmUI 0.9.8.4.GIT-abc{new string('x', 64 - 15)} reachable.", observation.Message);
        Assert.Equal(Instance().Id, observation.InstanceId);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"session_id":"s"}""")]
    [InlineData(HttpStatusCode.Found, """{"session_id":"s"}""")]
    [InlineData(HttpStatusCode.OK, "private detail")]
    [InlineData(HttpStatusCode.OK, """{"error":"private detail"}""")]
    [InlineData(HttpStatusCode.OK, """{"session_id":""}""")]
    public async Task ObserveAsync_ReportsGenericFailure(HttpStatusCode status, string payload)
    {
        using var client = new SwarmUIClient(new StubHandler((_, _) => Json(payload, status)));

        var observation = await client.ObserveAsync(Instance());

        Assert.False(observation.IsReachable);
        Assert.Equal("SwarmUI is not reachable.", observation.Message);
    }

    [Fact]
    public async Task ObserveAsync_ReportsTransportFailureAndTimeout()
    {
        using var failing = new SwarmUIClient(new StubHandler((_, _) => throw new HttpRequestException("private")));
        Assert.False((await failing.ObserveAsync(Instance())).IsReachable);
        using var slow = new SwarmUIClient(new StubHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }), TimeSpan.FromMilliseconds(50));
        Assert.False((await slow.ObserveAsync(Instance())).IsReachable);
    }

    private static SwarmUIClient Client(Func<string, string> list) => new(new StubHandler((request, body) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/GetNewSession")) return Json("""{"session_id":"s"}""");
        using var document = JsonDocument.Parse(body);
        return Json(list(document.RootElement.GetProperty("path").GetString()!));
    }));

    private static string Files(params (string Source, string? Metadata)[] files) =>
        JsonSerializer.Serialize(new { folders = Array.Empty<string>(), files = files.Select(file => new { src = file.Source, metadata = file.Metadata }) });

    private static DateTimeOffset Local(DateTime value) => new(value, TimeZoneInfo.Local.GetUtcOffset(value));

    private static SwarmUIInstance Instance(params SwarmUISearchPath[] paths) => new(
        Guid.Parse("6f3a0f8e-1d0e-4a59-9c84-0a5f0f6c2b11"), "Swarm", new Uri("https://swarm.example/prefix/"),
        paths.Length == 0 ? SwarmUIInstance.DefaultSearchPaths : paths);

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
            : this((request, body, _) => Task.FromResult(respond(request, body)))
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, body, cancellationToken);
        }
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
