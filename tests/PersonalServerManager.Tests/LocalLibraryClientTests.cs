using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Search;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LocalLibraryClientTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-local-client-").FullName;
    private readonly LocalLibraryClient _client = new();
    private LocalLibraryInstance Instance(LocalLibraryKind kind = LocalLibraryKind.Notes) => new(Guid.NewGuid(), "Fixture", _root, kind);

    [Fact]
    public async Task NotesExposeFileDatesAndRelativePath()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var path = Path.Combine(directory.FullName, "match.txt");
        await File.WriteAllTextAsync(path, "match");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var item = Assert.Single(await _client.SearchAsync(Instance(), "match", 5));

        Assert.Equal(new DateTimeOffset(File.GetCreationTimeUtc(path)), item.CreatedAt);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path)), item.UpdatedAt);
        Assert.Equal(Path.Combine("nested", "match.txt"), item.Path);
        Assert.Equal("TXT", item.FileFormat);
    }

    [Fact]
    public async Task NotesSearchesNamesAndContentsRecursivelyAndServesPlainText()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_root, "MATCH.md"), "<script>alert('fixture')</script>");
        await File.WriteAllTextAsync(Path.Combine(_root, "nested", "other.markdown"), "Mixed case mAtCh in content");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.html"), "match");
        var instance = Instance();
        var items = await _client.SearchAsync(instance, "match", 25);
        Assert.Equal(2, items.Count);
        Assert.Equal("MD", items.Single(item => item.Title == "MATCH.md").FileFormat);
        Assert.Equal("MARKDOWN", items.Single(item => item.Title == "other.markdown").FileFormat);
        Assert.All(items, item =>
        {
            Assert.Equal(LibraryMediaType.Note, item.MediaType);
            Assert.DoesNotContain(_root, item.ItemUrl!.ToString());
            Assert.Matches("^[a-f0-9]{64}$", item.Id);
            Assert.Equal($"/libraries/{instance.Id:D}/items/{item.Id}", item.ItemUrl.ToString());
        });
        await using var file = await _client.OpenItemAsync(instance, items.Single(item => item.Title == "MATCH.md").Id);
        Assert.NotNull(file);
        Assert.Equal("text/plain; charset=utf-8", file.ContentType);
        using var reader = new StreamReader(file.Content);
        Assert.StartsWith("<script>", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task NotesRejectsBinariesOversizeAndTreatsQueryLiterally()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "match.txt"), [0, 1, 2, 255]);
        await File.WriteAllTextAsync(Path.Combine(_root, "huge-match.md"), new string('a', 1024 * 1024 + 1));
        await File.WriteAllTextAsync(Path.Combine(_root, "literal.txt"), "literal %_ query");
        Assert.Empty(await _client.SearchAsync(Instance(), "match", 25));
        Assert.Single(await _client.SearchAsync(Instance(), "%_", 25));
        Assert.Empty(await _client.SearchAsync(Instance(), "*", 25));
        Assert.Null(await _client.OpenItemAsync(Instance(), "../literal.txt"));
    }

    [Fact]
    public async Task EmptyLimitAndCancellationAreBounded()
    {
        var instance = Instance();
        for (var index = 0; index < 30; index++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"fixture-{index}.txt"), "needle");
        Assert.Empty(await _client.SearchAsync(instance, " ", 25));
        Assert.Equal(25, (await _client.SearchAsync(instance, "needle", 25)).Count);
        Assert.Equal(5, (await _client.SearchAsync(instance, "needle", 5)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SearchAsync(instance, "needle", 26));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.SearchAsync(instance, "needle", 25, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.OpenItemAsync(instance, "1", cancellation.Token));
    }

    [Fact]
    public async Task CalibreReadsOptionalDatesAndFallsBackToPathForMissingTitle()
    {
        await CreateDatabaseAsync();
        await ExecuteAsync("""
            ALTER TABLE books ADD COLUMN timestamp TEXT;
            ALTER TABLE books ADD COLUMN last_modified TEXT;
            UPDATE books SET title = '', timestamp = '2020-01-02T03:04:05+00:00',
                last_modified = '2022-05-06T07:08:09+00:00' WHERE id = 1;
            """);
        var instance = Instance(LocalLibraryKind.CalibreText);
        var item = Assert.Single(await _client.SearchAsync(instance, "writer", 5));
        Assert.Equal("Writer/Fixture (1)", item.Title);
        Assert.Equal("Writer/Fixture (1)", item.Path);
        Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"), item.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2022-05-06T07:08:09Z"), item.UpdatedAt);

        await ExecuteAsync("UPDATE books SET timestamp = 'bad', last_modified = '2021-01-01T00:00:00Z' WHERE id = 1");
        item = Assert.Single(await _client.SearchAsync(instance, "writer", 5));
        Assert.Null(item.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2021-01-01T00:00:00Z"), item.UpdatedAt);
    }

    [Fact]
    public async Task CalibreSearchesMetadataEscapesSqlAndPrefersPrimaryFormatReadOnly()
    {
        await CreateDatabaseAsync();
        var database = Path.Combine(_root, "metadata.db");
        var before = await File.ReadAllBytesAsync(database);
        File.SetAttributes(database, FileAttributes.ReadOnly);
        var instance = Instance(LocalLibraryKind.CalibreText);
        foreach (var query in new[] { "fixture", "writer", "topic", "summary", "%_", "'" })
            Assert.Single(await _client.SearchAsync(instance, query, 25));
        Assert.Empty(await _client.SearchAsync(instance, "' OR 1=1 --", 25));
        Assert.Empty(await _client.SearchAsync(instance, "not-present", 25));
        var item = Assert.Single(await _client.SearchAsync(instance, "fixture", 25));
        Assert.Equal(LibraryMediaType.TextEbook, item.MediaType);
        Assert.Null(item.CreatedAt);
        Assert.Null(item.UpdatedAt);
        Assert.Equal("Writer/Fixture (1)", item.Path);
        Assert.Equal($"/libraries/{instance.Id:D}/covers/1", item.PreviewUrl!.ToString());
        await using (var file = await _client.OpenItemAsync(instance, item.Id))
        {
            Assert.NotNull(file);
            Assert.Equal("application/epub+zip", file.ContentType);
            Assert.Equal("book-1.epub", file.FileName);
            using var reader = new StreamReader(file.Content);
            Assert.Equal("epub fixture", await reader.ReadToEndAsync());
        }

        await using (var cover = await _client.OpenCoverAsync(instance, "1"))
        {
            Assert.NotNull(cover);
            Assert.Equal("image/jpeg", cover.ContentType);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        Assert.False(File.Exists(database + "-journal"));
        Assert.False(File.Exists(database + "-wal"));
        Assert.False(File.Exists(database + "-shm"));
    }

    [Theory]
    [InlineData("EPUB")]
    [InlineData("PDF")]
    public async Task CalibreSearchUsesRelativeReaderUrlsOnlyForRunningManagedInstances(string format)
    {
        await CreateDatabaseAsync();
        if (format == "PDF") await ExecuteAsync("DELETE FROM data WHERE book = 1 AND format = 'EPUB'");
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        var first = Instance(LocalLibraryKind.CalibreText) with { ManagedPort = port, CalibreInstallationFolder = _root };
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        using var http = new HttpClient(new CalibreReadyHandler(Path.GetFileName(_root)));
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, _ => Process.Start(
                new ProcessStartInfo("cmd.exe", "/d /c set /p fixture=")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true }));
        var client = new LocalLibraryClient(server);

        Assert.Null(Assert.Single(await client.SearchAsync(first, "fixture", 5)).ReaderUrl);
        Assert.Null(Assert.Single(await _client.SearchAsync(first, "fixture", 5)).ReaderUrl);
        foreach (var instance in new[] { first, second })
        {
            await server.StartServerAsync(instance.Id);
            Assert.NotNull(server.GetStatus(instance.Id).Url);
            var item = Assert.Single(await client.SearchAsync(instance, "fixture", 5));
            Assert.Equal(format, item.FileFormat);
            Assert.NotNull(item.ReaderUrl);
            Assert.False(item.ReaderUrl.IsAbsoluteUri);
            var prefix = $"/managed/calibre/{instance.Id:D}/";
            var expected = format == "PDF"
                ? prefix + "get/pdf/1/fixture-id?content_disposition=inline"
                : prefix + "#mode=read_book&library_id=fixture-id&book_id=1&fmt=epub";
            Assert.Equal(expected, item.ReaderUrl.ToString());
            await server.StopServerAsync(instance.Id);
        }

        var external = first with { ContentServerUrl = new Uri("https://calibre.example/reader/"), ContentServerLibraryId = "Books & Comics" };
        var externalItem = Assert.Single(await client.SearchAsync(external, "fixture", 5));
        Assert.NotNull(externalItem.ReaderUrl);
        Assert.True(externalItem.ReaderUrl.IsAbsoluteUri);
        var externalExpected = format == "PDF"
            ? "https://calibre.example/reader/get/pdf/1/Books%20%26%20Comics?content_disposition=inline"
            : "https://calibre.example/reader/#mode=read_book&library_id=Books%20%26%20Comics&book_id=1&fmt=epub";
        Assert.Equal(externalExpected, externalItem.ReaderUrl.AbsoluteUri);

        using var remoteHttp = new HttpClient(new CalibreRemoteHandler(format));
        var remoteClient = new LocalLibraryClient(server, remoteHttp);
        var remoteItem = Assert.Single(await remoteClient.SearchAsync(external with { RootPath = "" }, "fixture", 5));
        Assert.NotNull(remoteItem.ReaderUrl);
        Assert.True(remoteItem.ReaderUrl.IsAbsoluteUri);
        Assert.Equal(externalExpected, remoteItem.ReaderUrl.AbsoluteUri);
    }

    private sealed class CalibreReadyHandler(string libraryName) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    library_map = new Dictionary<string, string> { ["fixture-id"] = libraryName }
                }))
            });
    }

    private sealed class CalibreRemoteHandler(string format) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/ajax/search/")
                    ? "{\"book_ids\":[1],\"total_num\":1}"
                    : System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["1"] = new { title = "Fixture", formats = new[] { format } }
                    }))
            });
    }

    [Fact]
    public async Task ComicPrefersCbzAndRejectsTraversalAndUnsupportedFormats()
    {
        await CreateDatabaseAsync();
        var instance = Instance(LocalLibraryKind.CalibreComic);
        await ExecuteAsync("INSERT INTO data(book, name, format) VALUES (1, 'fixture', 'CBZ'), (1, 'fixture', 'CBR')");
        var mixed = await _client.SearchAsync(instance, "fixture", 25);
        Assert.Equal(2, mixed.Count);
        var item = Assert.Single(mixed, item => item.MediaType == LibraryMediaType.Comic);
        Assert.Equal("1", item.Id);
        Assert.Equal("1:EPUB", Assert.Single(mixed, item => item.MediaType == LibraryMediaType.TextEbook).Id);
        Assert.Equal(LibraryMediaType.Comic, item.MediaType);
        await using (var file = await _client.OpenItemAsync(instance, item.Id))
        {
            Assert.NotNull(file);
            Assert.Equal("book-1.cbz", file.FileName);
        }

        await ExecuteAsync("UPDATE books SET path = '../outside'");
        Assert.Empty(await _client.SearchAsync(instance, "fixture", 25));
        await Assert.ThrowsAsync<InvalidDataException>(() => _client.OpenCoverAsync(instance, "1"));
        Assert.Null(await _client.OpenItemAsync(instance, "1"));
        await ExecuteAsync("UPDATE books SET path = 'Writer/Fixture (1)'; UPDATE data SET name = '../escape'");
        Assert.Empty(await _client.SearchAsync(instance, "fixture", 25));
        await ExecuteAsync("UPDATE data SET name = 'fixture', format = 'HTML'");
        Assert.Empty(await _client.SearchAsync(instance, "fixture", 25));
        Assert.Null(await _client.OpenItemAsync(instance, "../1"));
        Assert.Null(await _client.OpenCoverAsync(instance, "1 OR 1=1"));
    }

    [Fact]
    public async Task NotesPagesMatchingNamesAndContentsInStableOrder()
    {
        var expectedNames = new List<string>();
        for (var index = 59; index >= 0; index--)
        {
            var name = index % 2 == 0 ? $"{index:D3}-content.txt" : $"{index:D3}-needle.txt";
            expectedNames.Add(name);
            await File.WriteAllTextAsync(Path.Combine(_root, name), index % 2 == 0 ? "Mixed NeEdLe content" : "name only");
            await File.WriteAllTextAsync(Path.Combine(_root, $"{index:D3}-ignored.txt"), "unrelated");
        }

        expectedNames.Sort(StringComparer.Ordinal);
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        for (var index = 4; index >= 0; index--)
            await File.WriteAllTextAsync(Path.Combine(nested, $"nested-{index}.txt"), "needle");
        expectedNames.AddRange(Enumerable.Range(0, 5).Select(index => $"nested-{index}.txt"));
        var instance = Instance();
        var first = await _client.SearchAsync(instance, "needle", 25);
        var second = await _client.SearchAsync(instance, "needle", 25, offset: 25);
        var third = await _client.SearchAsync(instance, "needle", 25, offset: 50);
        var combined = first.Concat(second).Concat(third).ToArray();
        Assert.Equal(25, first.Count);
        Assert.Equal(25, second.Count);
        Assert.Equal(15, third.Count);
        Assert.Equal(expectedNames, combined.Select(item => item.Title));
        Assert.Equal(65, combined.Select(item => item.Id).Distinct().Count());
        Assert.Equal(second, await _client.SearchAsync(instance, "needle", 25, offset: 25));
        Assert.Equal(first, await _client.SearchAsync(instance, "needle", 25, offset: 0));
        Assert.Empty(await _client.SearchAsync(instance, "needle", 25, offset: 65));
        Assert.Empty(await _client.SearchAsync(instance, "needle", 25, offset: 10000));
    }

    [Theory]
    [InlineData(LocalLibraryKind.Notes, -1)]
    [InlineData(LocalLibraryKind.Notes, 10001)]
    [InlineData(LocalLibraryKind.CalibreText, -1)]
    [InlineData(LocalLibraryKind.CalibreText, 10001)]
    [InlineData(LocalLibraryKind.CalibreComic, -1)]
    [InlineData(LocalLibraryKind.CalibreComic, 10001)]
    public async Task SearchRejectsInvalidOffsetsBeforeAccessingLibrary(LocalLibraryKind kind, int offset)
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _client.SearchAsync(Instance(kind), "needle", 25, offset: offset));
        Assert.Equal("offset", exception.ParamName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _client.SearchAsync(Instance(kind), "", 25, offset: offset));
    }

    [Theory]
    [InlineData(LocalLibraryKind.Notes)]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task PagedSearchHonorsCancellation(LocalLibraryKind kind)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _client.SearchAsync(Instance(kind), "needle", 25, cancellation.Token, 25));
    }

    [Theory]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task CalibrePagesValidBooksBeyond250WithinCandidateBoundWithoutMutations(LocalLibraryKind kind)
    {
        await CreateDatabaseAsync();
        await ExecuteAsync("""
            WITH RECURSIVE candidates(id) AS (SELECT 3 UNION ALL SELECT id + 1 FROM candidates WHERE id < 10003)
            INSERT INTO books SELECT id, 'Pagination fixture', 'Writer/Fixture (1)' FROM candidates;
            INSERT INTO data(book, name, format)
                SELECT id, CASE WHEN id % 2 = 0 OR id = 10003 THEN 'fixture' ELSE 'missing' END,
                    CASE WHEN id BETWEEN 302 AND 420 OR id = 10003 THEN 'PDF' ELSE 'HTML' END
                FROM books WHERE id >= 3;
            """);
        var database = Path.Combine(_root, "metadata.db");
        var before = await File.ReadAllBytesAsync(database);
        File.SetAttributes(database, FileAttributes.ReadOnly);
        var filesBefore = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var instance = Instance(kind);
        var first = await _client.SearchAsync(instance, "pagination", 25);
        var second = await _client.SearchAsync(instance, "pagination", 25, offset: 25);
        var third = await _client.SearchAsync(instance, "pagination", 25, offset: 50);
        var combined = first.Concat(second).Concat(third).ToArray();
        Assert.Equal(25, first.Count);
        Assert.Equal(25, second.Count);
        Assert.Equal(10, third.Count);
        Assert.Equal(Enumerable.Range(0, 60).Select(index => (302 + index * 2).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            combined.Select(item => item.Id));
        Assert.Equal(60, combined.Select(item => item.Id).Distinct().Count());
        Assert.Equal(second, await _client.SearchAsync(instance, "pagination", 25, offset: 25));
        Assert.Equal(first, await _client.SearchAsync(instance, "pagination", 25, offset: 0));
        Assert.Empty(await _client.SearchAsync(instance, "pagination", 25, offset: 60));
        Assert.Empty(await _client.SearchAsync(instance, "pagination", 25, offset: 10000));
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        Assert.Equal(filesBefore, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CalibreUsesHalfStarRatingsAndHidesZero()
    {
        await CreateDatabaseAsync();
        var instance = Instance(LocalLibraryKind.Calibre);
        Assert.Null(Assert.Single(await _client.SearchAsync(instance, "fixture", 5)).RatingOutOfFive);
        await ExecuteAsync("""
            CREATE TABLE ratings (id INTEGER PRIMARY KEY, rating INTEGER);
            CREATE TABLE books_ratings_link (book INTEGER, rating INTEGER);
            INSERT INTO ratings VALUES (1, 7), (2, 0);
            INSERT INTO books_ratings_link VALUES (1, 1), (2, 2);
            """);
        Assert.Equal(3.5m, Assert.Single(await _client.SearchAsync(instance, "fixture", 5)).RatingOutOfFive);
        Assert.Null(Assert.Single(await _client.SearchAsync(instance, "ordinary", 5)).RatingOutOfFive);
    }

    [Theory]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task CalibreDescriptionsNormalizeHtmlAndPreserveAuthors(LocalLibraryKind kind)
    {
        await CreateDatabaseAsync();
        await SetCommentAsync("<p>A &amp; B&nbsp; &#169; &#x1F600;</p><p>Second <b>paragraph</b><br>line</p>"
            + "<script>private script content</script><STYLE>private style content</STYLE><!--private comment-->"
            + "<p title=\"private attribute > content\">Last\t  paragraph</p>");
        var item = Assert.Single(await _client.SearchAsync(Instance(kind), "fixture", 25));
        Assert.Equal("A & B \u00a9 \U0001F600\n\nSecond paragraph\nline\n\nLast paragraph", item.Description);
        Assert.Equal("Writer", item.Detail);
        Assert.Equal(LibraryMediaType.TextEbook, item.MediaType);
    }

    [Theory]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task CalibreDescriptionsAllowMissingNullAndEmptyComments(LocalLibraryKind kind)
    {
        await CreateDatabaseAsync();
        foreach (var comment in new string?[] { null, "", " \t<p>&nbsp;</p><script>private</script><style>private</style>" })
        {
            await SetCommentAsync(comment);
            var item = Assert.Single(await _client.SearchAsync(Instance(kind), "fixture", 25));
            Assert.Null(item.Description);
            Assert.Equal("Writer", item.Detail);
        }
        await ExecuteAsync("DELETE FROM comments");
        Assert.Null(Assert.Single(await _client.SearchAsync(Instance(kind), "fixture", 25)).Description);
    }

    [Theory]
    [InlineData(LocalLibraryKind.CalibreText)]
    [InlineData(LocalLibraryKind.CalibreComic)]
    public async Task CalibreDescriptionsBoundOutputAndInputIncludingTruncatedUnsafeBlocks(LocalLibraryKind kind)
    {
        await CreateDatabaseAsync();
        var instance = Instance(kind);
        await SetCommentAsync(new string('a', 5000));
        Assert.Equal(new string('a', 4000), Assert.Single(await _client.SearchAsync(instance, "fixture", 25)).Description);
        await SetCommentAsync(new string(' ', 32768) + "outside query bound");
        Assert.Null(Assert.Single(await _client.SearchAsync(instance, "fixture", 25)).Description);
        foreach (var tag in new[] { "script", "style" })
        {
            await SetCommentAsync($"<p>Visible</p><{tag}>" + new string('x', 40000) + $"</{tag}>hidden tail");
            Assert.Equal("Visible", Assert.Single(await _client.SearchAsync(instance, "fixture", 25)).Description);
        }
        await SetCommentAsync(new string('a', 3999) + "\U0001F600tail");
        Assert.Equal(new string('a', 3999), Assert.Single(await _client.SearchAsync(instance, "fixture", 25)).Description);
    }

    [Theory]
    [InlineData(LibrarySort.Default, new[] { "3", "4", "5", "6", "7" })]
    [InlineData(LibrarySort.TitleAscending, new[] { "9", "7", "3", "5", "4" })]
    [InlineData(LibrarySort.TitleDescending, new[] { "8", "6", "4", "5", "3" })]
    [InlineData(LibrarySort.DateNewest, new[] { "9", "6", "4", "5", "3" })]
    [InlineData(LibrarySort.DateOldest, new[] { "8", "7", "3", "5", "4" })]
    public async Task CalibreSortsAllMatchesBeforeApplyingLimit(LibrarySort sort, string[] expected)
    {
        await CreateDatabaseAsync();
        await ExecuteAsync("""
            ALTER TABLE books ADD COLUMN timestamp TEXT;
            INSERT INTO books(id, title, path, timestamp) VALUES
                (3, 'Sorted Charlie', 'Writer/Fixture (1)', '2020-03-01T00:00:00+00:00'),
                (4, 'Sorted Echo', 'Writer/Fixture (1)', '2020-05-01T00:00:00+00:00'),
                (5, 'Sorted Delta', 'Writer/Fixture (1)', '2020-04-01T00:00:00+00:00'),
                (6, 'Sorted Foxtrot', 'Writer/Fixture (1)', '2020-06-01T00:00:00+00:00'),
                (7, 'Sorted Bravo', 'Writer/Fixture (1)', '2020-02-01T00:00:00+00:00'),
                (8, 'Sorted Golf', 'Writer/Fixture (1)', '2020-01-01T00:00:00+00:00'),
                (9, 'Sorted Alpha', 'Writer/Fixture (1)', '2020-07-01T00:00:00+00:00');
            INSERT INTO data(book, name, format) SELECT id, 'fixture', 'EPUB' FROM books WHERE id >= 3;
            """);

        var items = await _client.SearchAsync(Instance(LocalLibraryKind.CalibreText), "sorted", 5, sort: sort);

        Assert.Equal(expected, items.Select(item => item.Id));
    }

    [Fact]
    public async Task NotesTitleSortCoversMatchesBeyondTheFirstPage()
    {
        for (var index = 1; index <= 5; index++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"m{index}.txt"), "needle");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "a1.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(nested, "a2.txt"), "needle");
        var instance = Instance();

        var sorted = await _client.SearchAsync(instance, "needle", 5, sort: LibrarySort.TitleAscending);
        var unsorted = await _client.SearchAsync(instance, "needle", 5);

        Assert.Equal(new[] { "a1.txt", "a2.txt", "m1.txt", "m2.txt", "m3.txt" }, sorted.Select(item => item.Title));
        Assert.Equal(new[] { "m1.txt", "m2.txt", "m3.txt", "m4.txt", "m5.txt" }, unsorted.Select(item => item.Title));
    }

    private async Task SetCommentAsync(string? text)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "metadata.db"), Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE comments SET text = $text WHERE book = 1";
        command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateDatabaseAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Writer", "Fixture (1)")).FullName;
        foreach (var format in new[] { "epub", "pdf", "cbz", "cbr" })
            await File.WriteAllTextAsync(Path.Combine(directory, "fixture." + format), format + " fixture");
        await File.WriteAllBytesAsync(Path.Combine(directory, "cover.jpg"), [255, 216, 255, 217]);
        await ExecuteAsync("""
            CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT NOT NULL, path TEXT NOT NULL);
            CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER, name TEXT, format TEXT);
            CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE books_authors_link (book INTEGER, author INTEGER);
            CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE books_tags_link (book INTEGER, tag INTEGER);
            CREATE TABLE comments (book INTEGER, text TEXT);
            INSERT INTO books VALUES (1, 'Fixture %_ '' book', 'Writer/Fixture (1)'),
                (2, 'Other ordinary book', 'Writer/Fixture (1)');
            INSERT INTO data VALUES (1, 1, 'fixture', 'PDF'), (2, 1, 'fixture', 'EPUB'),
                (5, 2, 'fixture', 'EPUB');
            INSERT INTO authors VALUES (1, 'Writer');
            INSERT INTO books_authors_link VALUES (1, 1);
            INSERT INTO tags VALUES (1, 'Topic');
            INSERT INTO books_tags_link VALUES (1, 1);
            INSERT INTO comments VALUES (1, '<p>Summary</p>');
            """);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("Writer/../Fixture (1)")]
    [InlineData("Writer/Fixture (1):stream")]
    [InlineData("Writer/Fixture (1)\u0001")]
    public async Task CalibreRejectsUnsafeBookPaths(string path)
    {
        await CreateDatabaseAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "metadata.db"), Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE books SET path = $path";
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync();
        Assert.Empty(await _client.SearchAsync(Instance(LocalLibraryKind.CalibreText), "fixture", 25));
    }

    [Theory]
    [InlineData("../fixture", "EPUB")]
    [InlineData("sub/fixture", "EPUB")]
    [InlineData("fixture:stream", "EPUB")]
    [InlineData("fixture\u0001", "EPUB")]
    [InlineData("fixture", "../EPUB")]
    [InlineData("fixture", "HTML")]
    public async Task CalibreRejectsUnsafeDataNamesAndFormats(string name, string format)
    {
        await CreateDatabaseAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "metadata.db"), Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE data SET name = $name, format = $format";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$format", format);
        await command.ExecuteNonQueryAsync();
        Assert.Empty(await _client.SearchAsync(Instance(LocalLibraryKind.CalibreText), "fixture", 25));
        Assert.Null(await _client.OpenItemAsync(Instance(LocalLibraryKind.CalibreText), "1"));
    }

    [Fact]
    public async Task CalibreHonorsLimitAndDoesNotSearchEbookContents()
    {
        await CreateDatabaseAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "Writer", "Fixture (1)", "fixture.epub"), "ebook-body-only");
        for (var index = 3; index <= 31; index++)
            await ExecuteAsync($"INSERT INTO books VALUES ({index}, 'Fixture', 'Writer/Fixture (1)'); INSERT INTO data(book,name,format) VALUES ({index}, 'fixture','EPUB')");
        var instance = Instance(LocalLibraryKind.CalibreText);
        Assert.Equal(25, (await _client.SearchAsync(instance, "fixture", 25)).Count);
        Assert.Equal(5, (await _client.SearchAsync(instance, "fixture", 5)).Count);
        Assert.Empty(await _client.SearchAsync(instance, "ebook-body-only", 25));
        Assert.Empty(await _client.SearchAsync(instance, "", 25));
    }

    [Fact]
    public async Task NotesExcludesJunctionChildrenAndRejectsJunctionRoot()
    {
        var outside = Directory.CreateTempSubdirectory("psm-local-outside-").FullName;
        var link = Path.Combine(_root, "linked");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "fixture.txt"), "junction needle");
            CreateJunction(link, outside);
            Assert.Empty(await _client.SearchAsync(Instance(), "needle", 25));
            await Assert.ThrowsAsync<InvalidDataException>(() => _client.SearchAsync(Instance() with { RootPath = link }, "needle", 25));
            var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Instance() with { RootPath = link }));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task CalibreRejectsBookJunctionAndMetadataSymlinkWhenSupported()
    {
        await CreateDatabaseAsync();
        var original = Path.Combine(_root, "Writer", "Fixture (1)");
        var moved = Path.Combine(_root, "moved");
        Directory.Move(original, moved);
        try
        {
            CreateJunction(original, moved);
            Assert.Empty(await _client.SearchAsync(Instance(LocalLibraryKind.CalibreText), "fixture", 25));
            await Assert.ThrowsAsync<InvalidDataException>(() => _client.OpenCoverAsync(Instance(LocalLibraryKind.CalibreText), "1"));
        }
        finally
        {
            if (Directory.Exists(original)) Directory.Delete(original);
            Directory.Move(moved, original);
        }

        var database = Path.Combine(_root, "metadata.db");
        var renamed = Path.Combine(_root, "original.db");
        File.Move(database, renamed);
        try
        {
            try
            {
                File.CreateSymbolicLink(database, renamed);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => _client.SearchAsync(Instance(LocalLibraryKind.CalibreText), "fixture", 25));
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
            File.Move(renamed, database);
        }
    }

    [Fact]
    public async Task CoverRejectsHtmlAndOversizePayloads()
    {
        await CreateDatabaseAsync();
        var instance = Instance(LocalLibraryKind.CalibreText);
        var coverPath = Path.Combine(_root, "Writer", "Fixture (1)", "cover.jpg");
        await File.WriteAllTextAsync(coverPath, "<html>not an image</html>");
        Assert.Null(await _client.OpenCoverAsync(instance, "1"));
        await File.WriteAllBytesAsync(coverPath, new byte[5 * 1024 * 1024 + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _client.OpenCoverAsync(instance, "1"));
    }

    private static void CreateJunction(string link, string destination)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{destination}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "metadata.db"), Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        var database = Path.Combine(_root, "metadata.db");
        if (File.Exists(database)) File.SetAttributes(database, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}