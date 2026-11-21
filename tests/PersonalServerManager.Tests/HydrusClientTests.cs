using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class HydrusClientTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchTagsAsync_EncodesReadOnlyQueryAndClampsResults(int limit)
    {
        const string queryText = "creator:a & b+#%/\u9752";
        using var client = new HydrusClient(new StubHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/add_tags/search_tags", request.RequestUri!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal(queryText, query["search"].ToString());
            Assert.Equal("display", query["tag_display_type"].ToString());
            Assert.Equal(2, query.Count);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Hydrus-Client-API-Access-Key")));
            Assert.DoesNotContain("test-key", request.RequestUri.AbsoluteUri);
            Assert.Empty(body);
            return Json(JsonSerializer.Serialize(new { tags = Enumerable.Range(1, 30).Select(index => new { value = $"tag:{index}", count = 6000000000L + index }) }));
        }));

        var tags = await client.SearchTagsAsync(Instance(), queryText, limit);

        Assert.Equal(limit, tags.Count);
        Assert.Equal(new HydrusTag($"tag:{limit}", 6000000000L + limit), tags[^1]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tags\":null}")]
    [InlineData("{\"tags\":[{\"value\":\"tag\",\"count\":-1}]}")]
    [InlineData("{\"tags\":[{\"value\":\"tag\",\"count\":1.5}]}")]
    [InlineData("{\"tags\":[{\"value\":\"tag\",\"count\":\"2\"}]}")]
    [InlineData("{\"tags\":[{\"value\":null,\"count\":1}]}")]
    [InlineData("{\"tags\":[{\"value\":\" \",\"count\":1}]}")]
    [InlineData("{\"tags\":[null]}")]
    public async Task SearchTagsAsync_RejectsMalformedTagData(string response)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => Json(response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchTagsAsync(Instance(), "tag", 5));
    }

    [Fact]
    public async Task SearchTagsAsync_EmptyResultsAndZeroCountsAreValid()
    {
        using var empty = new HydrusClient(new StubHandler((_, _) => Json("""{"tags":[]}""")));
        Assert.Empty(await empty.SearchTagsAsync(Instance(), "tag", 5));
        using var zero = new HydrusClient(new StubHandler((_, _) => Json("""{"tags":[{"value":"tag","count":0}]}""")));
        Assert.Equal(new HydrusTag("tag", 0), Assert.Single(await zero.SearchTagsAsync(Instance(), "tag", 5)));
    }

    [Fact]
    public async Task SearchTagsAsync_ValidatesInputsAndCancellationBeforeSending()
    {
        using var client = new HydrusClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send.")));
        foreach (var limit in new[] { -1, 0, 6, 26 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchTagsAsync(Instance(), "tag", limit));
        foreach (var query in new[] { "", " ", new string('a', 1025) })
            await Assert.ThrowsAsync<ArgumentException>(() => client.SearchTagsAsync(Instance(), query, 5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchTagsAsync(Instance(), "tag", 5, cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task SearchTagsAsync_FailureDoesNotEchoSecretsOrFollowRedirects(HttpStatusCode status)
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent("test-key private data") };
            response.Headers.Location = new Uri("https://other.example/?secret=test-key");
            return response;
        }));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchTagsAsync(Instance(), "tag", 5));
        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain("test-key", failure.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SearchTagsAsync_DoesNotEchoMalformedJsonOrTransportErrors()
    {
        using var malformed = new HydrusClient(new StubHandler((_, _) => Json("test-key")));
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => malformed.SearchTagsAsync(Instance(), "tag", 5));
        Assert.DoesNotContain("test-key", invalid.ToString());
        using var failing = new HydrusClient(new StubHandler((_, _) => throw new HttpRequestException("test-key")));
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => failing.SearchTagsAsync(Instance(), "tag", 5));
        Assert.DoesNotContain("test-key", failed.ToString());
    }

    [Fact]
    public async Task SearchTagsAsync_CancellationCoversResponseBody()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HydrusClient(new StubHandler((_, _) =>
        {
            started.SetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) };
        }));
        var search = client.SearchTagsAsync(Instance(), "tag", 5, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task SearchTagsAsync_LegacyImplementationsReturnEmptyResults()
    {
        IHydrusClient legacy = new LegacyClient();
        Assert.Empty(await legacy.SearchTagsAsync(Instance(), "tag", 5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchTagsAsync_RejectsOversizedResponse(bool knownLength)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(knownLength
                ? new MemoryStream(new byte[16 * 1024 * 1024 + 1])
                : new NonSeekableStream(new byte[16 * 1024 * 1024 + 1]))
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchTagsAsync(Instance(), "tag", 5));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_RequestsAndClampsSelectedLimitIncludingMetadata(int limit)
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((request, _) =>
        {
            calls++;
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("search_files", StringComparison.Ordinal))
            {
                Assert.Equal(new[] { "tag", $"system:limit={limit}" }, JsonSerializer.Deserialize<string[]>(query["tags"].ToString()));
                return Json(JsonSerializer.Serialize(new { file_ids = Enumerable.Range(1, 30) }));
            }

            Assert.Equal(Enumerable.Range(1, limit).Select(index => (long)index), JsonSerializer.Deserialize<long[]>(query["file_ids"].ToString()));
            return Json(JsonSerializer.Serialize(new { metadata = Enumerable.Range(1, limit).Select(index => new { file_id = index, mime = "image/png" }) }));
        }));

        var files = await client.SearchAsync(Instance(), ["tag"], limit);

        Assert.Equal(2, calls);
        Assert.Equal(limit, files.Count);
        Assert.Equal(limit, files[^1].Id);
    }

    [Theory]
    [InlineData(LibrarySort.DateNewest, "&file_sort_type=2&file_sort_asc=false")]
    [InlineData(LibrarySort.DateOldest, "&file_sort_type=2&file_sort_asc=true")]
    [InlineData(LibrarySort.Random, "&file_sort_type=4")]
    [InlineData(LibrarySort.Default, null)]
    [InlineData(LibrarySort.TitleAscending, null)]
    [InlineData(LibrarySort.TitleDescending, null)]
    public async Task SearchAsync_SendsNativeFileSortOnlyForDateAndRandom(LibrarySort sort, string? expected)
    {
        var searches = 0;
        using var client = new HydrusClient(new StubHandler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("search_files", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("file_sort", request.RequestUri.Query);
                return Json("""{"metadata":[{"file_id":1,"mime":"image/png"}]}""");
            }

            searches++;
            var query = request.RequestUri.Query;
            if (expected is null) Assert.DoesNotContain("file_sort_type", query);
            else Assert.Contains(expected, query);
            if (sort == LibrarySort.Random) Assert.DoesNotContain("file_sort_asc", query);
            return Json("""{"file_ids":[1]}""");
        }));

        Assert.Single(await client.SearchAsync(Instance(), ["tag"], 5, sort));
        Assert.Equal(1, searches);
    }

    [Theory]
    [InlineData(LibrarySort.TitleAscending, new[] { "alpha", "bravo", "charlie", "delta", "echo" })]
    [InlineData(LibrarySort.TitleDescending, new[] { "golf", "foxtrot", "echo", "delta", "charlie" })]
    [InlineData(LibrarySort.Default, new[] { "delta", "golf", "alpha", "echo", "charlie" })]
    public async Task SearchTagsAsync_SortsEveryReturnedTagBeforeApplyingLimit(LibrarySort sort, string[] expected)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => Json(JsonSerializer.Serialize(new
        {
            tags = new[] { "delta", "golf", "alpha", "echo", "charlie", "foxtrot", "bravo" }.Select((value, index) => new { value, count = index })
        }))));

        var tags = await client.SearchTagsAsync(Instance(), "tag", 5, sort);

        Assert.Equal(expected, tags.Select(tag => tag.Name));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    public async Task SearchAsync_RejectsMetadataBeyondSelectedBound(int limit)
    {
        using var client = new HydrusClient(new StubHandler((request, _) =>
            Json(request.RequestUri!.AbsolutePath.EndsWith("search_files", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { file_ids = Enumerable.Range(1, limit) })
                : JsonSerializer.Serialize(new { metadata = Enumerable.Range(1, limit + 1).Select(index => new { file_id = index, mime = "image/png" }) }))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Instance(), ["tag"], limit));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(26)]
    public async Task SearchAsync_RejectsUnsupportedLimitsWithoutSending(int limit)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send.")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync(Instance(), ["tag"], limit));
    }

    [Fact]
    public async Task ObserveAsync_VerifiesAccessUsingOnlyTheHeader()
    {
        using var client = new HydrusClient(new StubHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/verify_access_key", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Hydrus-Client-API-Access-Key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("", request.RequestUri.Query);
            Assert.Equal("", body);
            return Json("""{"permits_everything":false,"basic_permissions":[3]}""");
        }));

        var result = await client.ObserveAsync(Instance());

        Assert.True(result.IsReachable);
        Assert.Equal(Instance().Id, result.InstanceId);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToBasicMetadataWhenFullResponseIsTooLarge()
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((request, _) =>
        {
            calls++;
            if (calls == 1) return Json("""{"file_ids":[1]}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (calls == 2)
            {
                Assert.Equal("false", query["only_return_basic_information"].ToString());
                return Json(new string(' ', 256 * 1024 + 1));
            }
            Assert.Equal("true", query["only_return_basic_information"].ToString());
            return Json("""{"metadata":[{"file_id":1,"mime":"image/png"}]}""");
        }));

        var file = Assert.Single(await client.SearchAsync(Instance(), ["tag"]));
        Assert.Equal(3, calls);
        Assert.Null(file.CreatedAt);
        Assert.Null(file.UpdatedAt);
    }

    [Fact]
    public async Task SearchAsync_MapsImportAndModificationDatesAndIgnoresInvalidValues()
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((_, _) => ++calls == 1
            ? Json("""{"file_ids":[1,2]}""")
            : Json("""{"metadata":[{"file_id":1,"mime":"image/png","time_modified":1609459200,"file_services":{"current":{"first":{"time_imported":1577836800},"second":{"time_imported":1600000000}}}},{"file_id":2,"mime":"image/png","time_modified":999999999999999,"file_services":{"current":{"first":{"time_imported":"bad"}}}}]}""")));

        var files = await client.SearchAsync(Instance(), ["tag"]);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1577836800), files[0].CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1609459200), files[0].UpdatedAt);
        Assert.Null(files[0].Path);
        Assert.Null(files[1].CreatedAt);
        Assert.Null(files[1].UpdatedAt);
    }

    [Fact]
    public async Task SearchAsync_UsesServerSideLimitAndBoundedMetadataWithoutOpeningAPage()
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((request, body) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Hydrus-Client-API-Access-Key")));
            Assert.DoesNotContain("test-key", request.RequestUri!.AbsoluteUri);
            Assert.Empty(body);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (calls == 1)
            {
                Assert.Equal("/get_files/search_files", request.RequestUri.AbsolutePath);
                Assert.Equal(new[] { "creator:some person", "-tag & other", "system:limit=5" },
                    JsonSerializer.Deserialize<string[]>(query["tags"].ToString()));
                Assert.False(query.ContainsKey("limit"));
                return Json("""{"file_ids":[6000000001,2,3,4,5,6]}""");
            }

            Assert.Equal("/get_files/file_metadata", request.RequestUri.AbsolutePath);
            Assert.Equal(new long[] { 6000000001, 2, 3, 4, 5 }, JsonSerializer.Deserialize<long[]>(query["file_ids"].ToString()));
            Assert.Equal("false", query["only_return_basic_information"].ToString());
            Assert.Equal("false", query["include_services_object"].ToString());
            return Json("""{"metadata":[{"file_id":2,"mime":"video/webm"},{"file_id":6000000001,"mime":"image/jpeg"}]}""");
        }));

        var files = await client.SearchAsync(Instance(), ["creator:some person", "-tag & other"]);

        Assert.Equal(2, calls);
        Assert.Equal(new HydrusFile(6000000001, "File 6000000001", "image/jpeg"), files[0]);
        Assert.Equal(new HydrusFile(2, "File 2", "video/webm"), files[1]);
    }

    [Fact]
    public async Task SearchAsync_ConcurrentCallsKeepSearchAndMetadataPairsTogether()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var metadataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HydrusClient(new StubHandler(async (request, _, cancellationToken) =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath == "/get_files/search_files")
            {
                var tags = JsonSerializer.Deserialize<string[]>(query["tags"].ToString())!;
                var first = tags[0] == "first";
                calls.Enqueue(first ? "search1" : "search2");
                if (!first)
                {
                    Assert.True(metadataCompleted.Task.IsCompleted);
                }

                return Json(first ? """{"file_ids":[1]}""" : """{"file_ids":[2]}""");
            }

            Assert.Equal("/get_files/file_metadata", request.RequestUri.AbsolutePath);
            var identifier = Assert.Single(JsonSerializer.Deserialize<long[]>(query["file_ids"].ToString())!);
            calls.Enqueue($"metadata{identifier}");
            if (identifier == 1)
            {
                metadataStarted.SetResult();
                await releaseMetadata.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                metadataCompleted.SetResult();
            }

            return Json(identifier == 1
                ? """{"metadata":[{"file_id":1,"mime":"image/png"}]}"""
                : """{"metadata":[{"file_id":2,"mime":"video/webm"}]}""");
        }));

        var firstSearch = client.SearchAsync(Instance(), ["first"]);
        try
        {
            await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var secondSearch = client.SearchAsync(Instance(), ["second"]);

            Assert.False(firstSearch.IsCompleted);
            Assert.False(secondSearch.IsCompleted);
            Assert.Equal(new[] { "search1", "metadata1" }, calls.ToArray());

            releaseMetadata.SetResult();
            var results = await Task.WhenAll(firstSearch, secondSearch).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { "search1", "metadata1", "search2", "metadata2" }, calls.ToArray());
            Assert.Equal(new HydrusFile(1, "File 1", "image/png"), Assert.Single(results[0]));
            Assert.Equal(new HydrusFile(2, "File 2", "video/webm"), Assert.Single(results[1]));
        }
        finally
        {
            releaseMetadata.TrySetResult();
        }
    }

    [Fact]
    public async Task SearchAsync_PreservesCallerCancellationWhileQueued()
    {
        var metadataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new HydrusClient(new StubHandler(async (request, _, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath == "/get_files/search_files")
            {
                return Json("""{"file_ids":[1]}""");
            }

            metadataStarted.SetResult();
            await releaseMetadata.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return Json("""{"metadata":[{"file_id":1,"mime":"image/png"}]}""");
        }));

        var firstSearch = client.SearchAsync(Instance(), ["first"]);
        try
        {
            await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queuedSearch = client.SearchAsync(Instance(), ["second"], cancellation.Token);
            Assert.False(queuedSearch.IsCompleted);

            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queuedSearch.WaitAsync(TimeSpan.FromSeconds(3)));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(firstSearch.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref calls));
            releaseMetadata.SetResult();
            Assert.Single(await firstSearch.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            releaseMetadata.TrySetResult();
        }
    }

    [Fact]
    public async Task SearchAsync_EmptyResultsSkipMetadata()
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((_, _) => { calls++; return Json("""{"file_ids":[]}"""); }));

        Assert.Empty(await client.SearchAsync(Instance(), ["tag"]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OpenSearchAsync_ExplicitlyCreatesAndFocusesDesktopPageWithFullQuery()
    {
        using var client = new HydrusClient(new StubHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/manage_pages/new_page", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Hydrus-Client-API-Access-Key")));
            Assert.Empty(request.RequestUri.Query);
            Assert.DoesNotContain("test-key", body);
            using var payload = JsonDocument.Parse(body);
            Assert.Equal(6, payload.RootElement.GetProperty("page_type").GetInt32());
            Assert.True(payload.RootElement.GetProperty("focus_page").GetBoolean());
            Assert.Equal(new[] { "creator:some person", "system:width>1000" },
                payload.RootElement.GetProperty("tags").Deserialize<string[]>());
            return Json("""{"page_key":"abcdef012345"}""");
        }));

        Assert.Equal("abcdef012345", await client.OpenSearchAsync(Instance(), ["creator:some person", "system:width>1000"]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task SearchAsync_DoesNotEchoErrorBodiesOrFollowRedirects(HttpStatusCode status)
    {
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent("test-key private server error") };
            response.Headers.Location = new Uri("https://other.example/?secret=test-key");
            return response;
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(Instance(), ["tag"]));

        Assert.DoesNotContain("test-key", exception.ToString());
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ObserveAsync_DoesNotEchoTransportErrors()
    {
        using var client = new HydrusClient(new StubHandler((_, _) => throw new HttpRequestException("test-key")));
        var result = await client.ObserveAsync(Instance());
        Assert.False(result.IsReachable);
        Assert.DoesNotContain("test-key", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchAsync_RejectsOversizedResponse(bool knownLength)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(knownLength
                ? new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 256 * 1024 + 1)))
                : new NonSeekableStream(Encoding.UTF8.GetBytes(new string(' ', 256 * 1024 + 1))))
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Instance(), ["tag"]));
    }

    [Theory]
    [InlineData("{\"metadata\":[{\"file_id\":99,\"mime\":\"image/png\"}]}")]
    [InlineData("{\"metadata\":[{\"file_id\":1,\"mime\":null}]}")]
    [InlineData("{\"metadata\":[{\"file_id\":1,\"mime\":\"image/png\"},{\"file_id\":1,\"mime\":\"image/png\"}]}")]
    public async Task SearchAsync_RejectsInvalidMetadata(string metadata)
    {
        using var client = new HydrusClient(new StubHandler((request, _) =>
            Json(request.RequestUri!.AbsolutePath.EndsWith("search_files", StringComparison.Ordinal) ? """{"file_ids":[1]}""" : metadata)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Instance(), ["tag"]));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("observe")]
    [InlineData("open")]
    [InlineData("tags")]
    public async Task DeadlineCoversResponseBody(string operation)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream())
        }), TimeSpan.FromMilliseconds(100));

        if (operation == "observe")
        {
            Assert.False((await client.ObserveAsync(Instance()).WaitAsync(TimeSpan.FromSeconds(3))).IsReachable);
            return;
        }

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => (operation switch
        {
            "open" => (Task)client.OpenSearchAsync(Instance(), ["tag"]),
            "tags" => client.SearchTagsAsync(Instance(), "tag", 5),
            _ => client.SearchAsync(Instance(), ["tag"])
        }).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public void Constructor_DisablesRedirectsAndCookiesOnProductionTransports()
    {
        var socketsHandler = new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = true };
        using var socketsClient = new HydrusClient(socketsHandler);
        Assert.False(socketsHandler.AllowAutoRedirect);
        Assert.False(socketsHandler.UseCookies);

        var clientHandler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true };
        using var client = new HydrusClient(clientHandler);
        Assert.False(clientHandler.AllowAutoRedirect);
        Assert.False(clientHandler.UseCookies);
    }

    [Fact]
    public async Task SearchAsync_DoesNotEchoMalformedJsonOrTransportErrors()
    {
        using var malformedClient = new HydrusClient(new StubHandler((_, _) => Json("test-key")));
        var malformed = await Assert.ThrowsAsync<InvalidDataException>(() => malformedClient.SearchAsync(Instance(), ["tag"]));
        Assert.DoesNotContain("test-key", malformed.ToString());

        using var failingClient = new HydrusClient(new StubHandler((_, _) => throw new HttpRequestException("test-key")));
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => failingClient.SearchAsync(Instance(), ["tag"]));
        Assert.DoesNotContain("test-key", failed.ToString());
    }

    [Theory]
    [InlineData("https://user:password@hydrus.example/")]
    [InlineData("https://hydrus.example/?key=private")]
    [InlineData("file:///local")]
    public async Task SearchAsync_RejectsUnsafeEndpointsWithoutSending(string endpoint)
    {
        using var client = new HydrusClient(new StubHandler((_, _) => throw new InvalidOperationException("Must not send.")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(Instance() with { ApiEndpoint = new Uri(endpoint) }, ["tag"]));
    }

    [Fact]
    public async Task ObserveAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HydrusClient(new StubHandler((_, _) => Json("{}")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ObserveAsync(Instance(), cancellation.Token));
    }

    [Fact]
    public async Task Store_ProtectsKeysPreservesBlankEditsAndRemovesInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hydrus-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hydrus.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("Hydrus.Tests");
        var store = new JsonHydrusInstanceStore(path, protector);
        try
        {
            Assert.Empty(await store.GetAllAsync());
            await store.SaveAsync(Instance());
            Assert.DoesNotContain("test-key", await File.ReadAllTextAsync(path));
            await store.SaveAsync(Instance() with { Name = "Renamed", ApiKey = "" });
            var saved = Assert.Single(await new JsonHydrusInstanceStore(path, protector).GetAllAsync());
            Assert.Equal("test-key", saved.ApiKey);
            Assert.Equal("Renamed", saved.Name);
            Assert.Single(Directory.GetFiles(directory));
            await store.RemoveAsync(saved.Id);
            Assert.Empty(await store.GetAllAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Store_EncryptsApiKeyAndRoundTripsEveryField()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hydrus-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hydrus.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("Hydrus.Tests");
        var instance = Instance();
        try
        {
            await new JsonHydrusInstanceStore(path, protector).SaveAsync(instance);

            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(instance.ApiKey!, persisted);
            using var document = JsonDocument.Parse(persisted);
            var protectedKey = document.RootElement[0].GetProperty("protectedApiKey").GetString();
            Assert.False(string.IsNullOrWhiteSpace(protectedKey));
            Assert.NotEqual(instance.ApiKey, protectedKey);
            Assert.Equal(instance.ApiKey, protector.Unprotect(protectedKey!));

            var reloaded = new JsonHydrusInstanceStore(path, protector);
            Assert.Equal(instance, Assert.Single(await reloaded.GetAllAsync()));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task Store_BlankKeyEditPreservesProtectedKeyAfterReload(string? blankKey)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hydrus-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hydrus.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("Hydrus.Tests");
        try
        {
            await new JsonHydrusInstanceStore(path, protector).SaveAsync(Instance());
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var protectedKey = original.RootElement[0].GetProperty("protectedApiKey").GetString();
            var edited = Instance() with { Name = "Renamed", ApiEndpoint = new Uri("https://other.example/"), ApiKey = blankKey };

            await new JsonHydrusInstanceStore(path, protector).SaveAsync(edited);

            var persisted = await File.ReadAllTextAsync(path);
            using var updated = JsonDocument.Parse(persisted);
            Assert.Equal(protectedKey, updated.RootElement[0].GetProperty("protectedApiKey").GetString());
            Assert.DoesNotContain(Instance().ApiKey!, persisted);
            Assert.Equal(edited with { ApiKey = Instance().ApiKey },
                Assert.Single(await new JsonHydrusInstanceStore(path, protector).GetAllAsync()));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Theory]
    [InlineData("{invalid json")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"name\":null,\"apiEndpoint\":\"https://hydrus.example/\"}]")]
    [InlineData("[{\"name\":\"Invalid\",\"apiEndpoint\":\"not a URI\"}]")]
    [InlineData("[{\"name\":\"Invalid\",\"apiEndpoint\":\"https://hydrus.example/?key=private\"}]")]
    public async Task Store_InvalidFileRemainsUntouchedByReadSaveAndRemove(string invalidConfiguration)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hydrus-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hydrus.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("Hydrus.Tests");
        try
        {
            Directory.CreateDirectory(directory);
            var originalBytes = Encoding.UTF8.GetBytes(invalidConfiguration);
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new JsonHydrusInstanceStore(path, protector);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Instance()));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(Instance().Id));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Store_RemoveOnlyForgetsSelectedInstanceWithoutAffectingExternalClient()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hydrus-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "hydrus.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("Hydrus.Tests");
        var calls = 0;
        using var client = new HydrusClient(new StubHandler((request, body) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/verify_access_key", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Hydrus-Client-API-Access-Key")));
            Assert.Empty(body);
            return Json("""{"permits_everything":false,"basic_permissions":[3]}""");
        }));
        try
        {
            var store = new JsonHydrusInstanceStore(path, protector);
            var instance = Instance();
            var otherInstance = instance with { Id = Guid.NewGuid(), Name = "Other Hydrus", ApiKey = "other-test-key" };
            await store.SaveAsync(instance);
            await store.SaveAsync(otherInstance);
            var saved = Assert.Single(await store.GetAllAsync(), item => item.Id == instance.Id);
            Assert.True((await client.ObserveAsync(saved)).IsReachable);
            Assert.Equal(1, calls);

            await store.RemoveAsync(saved.Id);

            Assert.Equal(1, calls);
            Assert.Equal(otherInstance, Assert.Single(await new JsonHydrusInstanceStore(path, protector).GetAllAsync()));
            Assert.True((await client.ObserveAsync(saved)).IsReachable);
            Assert.Equal(2, calls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class LegacyClient : IHydrusClient
    {
        public Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static HydrusInstance Instance() => new(
        Guid.Parse("c7a6a4c0-c79f-4e3b-8aa9-8665d0564cbd"), "Primary Hydrus", new Uri("https://hydrus.example/"), "test-key");

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
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

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}