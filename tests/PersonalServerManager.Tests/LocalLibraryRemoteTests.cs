using System.Net;
using System.Text;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Search;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LocalLibraryRemoteTests
{
    private static LocalLibraryInstance External(LocalLibraryKind kind = LocalLibraryKind.Calibre) =>
        new(Guid.NewGuid(), "Remote", "", kind, new Uri("https://calibre.example/prefix/"), "Books & Comics");

    private const string Metadata = """
        {"title":"Mixed book","authors":["Writer"],"formats":["epub","cbz","pdf","markdown"],
         "comments":"<p>Summary</p><script>hidden</script>","rating":4.5,"timestamp":"2020-01-02T00:00:00Z"}
        """;

    [Theory]
    [InlineData(LocalLibraryKind.Calibre)]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task RootlessSearchUsesServerMetadataAndActualFormats(LocalLibraryKind kind)
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(request.RequestUri!.AbsolutePath.Contains("/ajax/search/")
                ? "{\"book_ids\":[42],\"total_num\":1}" : "{\"42\":" + Metadata + "}");
        }));
        var instance = External(kind);
        var client = new LocalLibraryClient(httpClient: http);
        var items = await client.SearchAsync(instance, "writer & title", 25);
        Assert.Equal(2, items.Count);
        var ebook = Assert.Single(items, item => item.MediaType == LibraryMediaType.TextEbook);
        var comic = Assert.Single(items, item => item.MediaType == LibraryMediaType.Comic);
        Assert.Equal(kind == LocalLibraryKind.CalibreComic ? "42:EPUB" : "42", ebook.Id);
        Assert.Equal(kind == LocalLibraryKind.CalibreComic ? "42" : "42:CBZ", comic.Id);
        Assert.Equal("Summary", ebook.Description);
        Assert.All(items, item => Assert.Equal(4.5m, item.RatingOutOfFive));
        Assert.Equal(DateTimeOffset.Parse("2020-01-02T00:00:00Z"), ebook.CreatedAt);
        Assert.Contains("fmt=cbz", comic.ReaderUrl!.AbsoluteUri);
        Assert.All(requests, request => Assert.StartsWith("https://calibre.example/prefix/ajax/", request.AbsoluteUri));
        Assert.Contains("Books%20%26%20Comics", requests[0].AbsoluteUri);
        Assert.Contains("writer%20%26%20title", requests[0].Query);
        Assert.Equal(items.Skip(1).Select(item => item.Id),
            (await client.SearchAsync(instance, "writer & title", 1, offset: 1)).Select(item => item.Id));
    }

    [Fact]
    public async Task EmptyQueryBrowsesAndSkipsUnsupportedBooksAcrossServerPages()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/ajax/search/")) return Json(url.Contains("offset=0")
                ? "{\"book_ids\":[1],\"total_num\":2}" : "{\"book_ids\":[2],\"total_num\":2}");
            return Json(url.Contains("ids=1") ? "{\"1\":{\"formats\":[\"html\"]}}"
                : "{\"2\":{\"title\":\"Markdown\",\"formats\":[\"markdown\"]}}");
        }));
        var item = Assert.Single(await new LocalLibraryClient(httpClient: http).SearchAsync(External(), "", 1));
        Assert.Equal("2", item.Id);
        Assert.Equal(LibraryMediaType.TextEbook, item.MediaType);
    }

    [Fact]
    public async Task DownloadsRequestedMixedFormatAndValidatesCover()
    {
        var downloads = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/ajax/book/")) return Json(Metadata);
            downloads.Add(path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = path.Contains("/get/cover/")
                ? new ByteArrayContent([255, 216, 255, 217]) : new StringContent("comic bytes") };
        }));
        var client = new LocalLibraryClient(httpClient: http);
        await using var file = await client.OpenItemAsync(External(), "42:CBZ");
        Assert.NotNull(file);
        Assert.Equal("book-42.cbz", file.FileName);
        using var reader = new StreamReader(file.Content);
        Assert.Equal("comic bytes", await reader.ReadToEndAsync());
        Assert.Contains("/prefix/get/cbz/42/Books%20%26%20Comics", downloads);
        await using var cover = await client.OpenCoverAsync(External(), "42");
        Assert.NotNull(cover);
        Assert.Equal("image/jpeg", cover.ContentType);
        Assert.Null(await client.OpenItemAsync(External(), "42:EXE"));
        Assert.Null(await client.OpenItemAsync(External(), "../42"));
    }

    [Fact]
    public async Task RemoteErrorsCancellationAndInvalidCoversAreNotSilentlyAccepted()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        var client = new LocalLibraryClient(httpClient: http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(External(), "test", 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync(External(), "", 1, cancellation.Token));
        using var invalidCoverHttp = new HttpClient(new Handler(_ => Json("<html>not a cover</html>")));
        Assert.Null(await new LocalLibraryClient(httpClient: invalidCoverHttp).OpenCoverAsync(External(), "42"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}