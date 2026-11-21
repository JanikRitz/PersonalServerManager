using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PersonalServerManager.Search;

namespace PersonalServerManager.LocalLibraries;

public sealed record LocalLibraryFile(Stream Content, string ContentType, string? FileName) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed class LocalLibraryClient(ManagedCalibreServer? managedCalibre = null, HttpClient? httpClient = null)
{
    private static readonly HttpClient DefaultHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http = httpClient ?? DefaultHttp;
    private const int MaximumNoteBytes = 1024 * 1024;
    private const int MaximumScanBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private const int MaximumDescriptionLength = 4000;
    private const int MaximumCommentLength = 32768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> NoteExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };
    private static readonly string[] TextFormats = ["EPUB", "PDF", "AZW3", "MOBI", "AZW", "FB2", "MD", "MARKDOWN", "TXT", "RTF", "DOCX", "ODT", "DJVU"];
    private static readonly string[] ComicFormats = ["CBZ", "CBR", "CB7", "CBT", "CBC"];

    public async Task<IReadOnlyList<LibraryItem>> SearchAsync(LocalLibraryInstance instance, string query, int limit,
        CancellationToken cancellationToken = default, int offset = 0, LibrarySort sort = LibrarySort.Default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset is < 0 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(offset));
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 1024 || query.Any(char.IsControl)) throw new ArgumentException("Invalid search query.", nameof(query));
        if (!Enum.IsDefined(instance.Kind)) throw new InvalidDataException("Invalid library kind.");
        if (instance.IsExternal)
            return await SearchRemoteAsync(instance, query.Trim(), limit, cancellationToken, offset, sort);
        if (string.IsNullOrWhiteSpace(query)) return [];
        var root = LocalLibraryPaths.Root(instance.RootPath);
        if (instance.Kind == LocalLibraryKind.Notes)
            return await SearchNotesAsync(instance, root, query.Trim(), limit, cancellationToken, offset, sort);
        try
        {
            return await SearchCalibreAsync(managedCalibre?.WithReaderConnection(instance) ?? instance,
                root, query.Trim(), limit, cancellationToken, offset, sort);
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public async Task<LocalLibraryFile?> OpenItemAsync(LocalLibraryInstance instance, string itemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instance.IsExternal) return await OpenRemoteItemAsync(instance, itemId, cancellationToken);
        var root = LocalLibraryPaths.Root(instance.RootPath);
        if (instance.Kind == LocalLibraryKind.Notes)
        {
            if (itemId.Length != 64 || itemId.Any(character => !char.IsAsciiHexDigit(character))) return null;
            foreach (var path in EnumerateNotes(root, cancellationToken))
            {
                if (!NoteId(root, path).Equals(itemId, StringComparison.Ordinal)) continue;
                var text = await ReadNoteAsync(root, path, cancellationToken);
                return text is null ? null : new LocalLibraryFile(new MemoryStream(StrictUtf8.GetBytes(text)),
                    "text/plain; charset=utf-8", null);
            }

            return null;
        }

        if (!instance.IsCalibre || !TryItemId(itemId, out var bookId, out var requestedFormat)) return null;
        await using var database = await OpenDatabaseAsync(root, cancellationToken);
        var connection = database.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, path FROM books WHERE id = $id";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var bookPath = reader.GetString(2);
        var file = await FindFormatAsync(connection, root, bookPath, bookId,
            requestedFormat is not null ? ComicFormats.Contains(requestedFormat) ? LocalLibraryKind.CalibreComic : LocalLibraryKind.CalibreText
                : instance.Kind == LocalLibraryKind.CalibreComic ? LocalLibraryKind.CalibreComic : LocalLibraryKind.CalibreText,
            cancellationToken, requestedFormat);
        if (file is null && requestedFormat is null)
            file = await FindFormatAsync(connection, root, bookPath, bookId,
                instance.Kind == LocalLibraryKind.CalibreComic ? LocalLibraryKind.CalibreText : LocalLibraryKind.CalibreComic, cancellationToken);
        if (file is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return new LocalLibraryFile(OpenRead(root, file.Value.Path), ContentType(file.Value.Format), $"book-{bookId}.{file.Value.Format.ToLowerInvariant()}");
    }

    public async Task<LocalLibraryFile?> OpenCoverAsync(LocalLibraryInstance instance, string bookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!instance.IsCalibre
            || !TryBookId(bookId, out var numericId)) return null;
        if (instance.IsExternal) return await OpenRemoteFileAsync(instance, numericId, "cover", cancellationToken);
        var root = LocalLibraryPaths.Root(instance.RootPath);
        await using var database = await OpenDatabaseAsync(root, cancellationToken);
        var connection = database.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM books WHERE id = $id";
        command.Parameters.AddWithValue("$id", numericId);
        var bookPath = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (bookPath is null) return null;
        return await ReadCoverAsync(root, bookPath, cancellationToken);
    }

    private static bool TryItemId(string value, out long bookId, out string? format)
    {
        var parts = value.Split(':');
        format = parts.Length == 2 ? parts[1].ToUpperInvariant() : null;
        bookId = 0;
        return parts.Length <= 2 && TryBookId(parts[0], out bookId) &&
            (format is null || TextFormats.Contains(format) || ComicFormats.Contains(format));
    }

    private static Uri RemoteUrl(LocalLibraryInstance instance, string route)
    {
        if (!instance.HasValidReaderConfiguration() || instance.ContentServerUrl is null)
            throw new InvalidDataException("Invalid Calibre Content Server configuration.");
        return new Uri(new Uri(instance.ContentServerUrl.AbsoluteUri.TrimEnd('/') + "/"), route);
    }

    private async Task<JsonDocument> ReadRemoteJsonAsync(LocalLibraryInstance instance, string route, CancellationToken token)
    {
        using var response = await _http.GetAsync(RemoteUrl(instance, route), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return JsonDocument.Parse(await ReadBoundedAsync(stream, 8 * 1024 * 1024, token));
    }

    private async Task<IReadOnlyList<LibraryItem>> SearchRemoteAsync(LocalLibraryInstance instance, string query,
        int limit, CancellationToken token, int offset, LibrarySort sort)
    {
        var library = Uri.EscapeDataString(instance.ContentServerLibraryId!);
        var field = sort is LibrarySort.DateNewest or LibrarySort.DateOldest ? "timestamp" :
            sort == LibrarySort.Default ? "id" : "title";
        var direction = sort is LibrarySort.TitleDescending or LibrarySort.DateNewest ? "desc" : "asc";
        var items = new List<LibraryItem>();
        for (var candidateOffset = 0; candidateOffset < MaximumEntries;)
        {
            using var search = await ReadRemoteJsonAsync(instance,
                $"ajax/search/{library}?query={Uri.EscapeDataString(query)}&num=100&offset={candidateOffset}&sort={field}&sort_order={direction}", token);
            var ids = search.RootElement.GetProperty("book_ids").EnumerateArray().Select(value => value.GetInt64())
                .Where(id => id > 0).Take(Math.Min(100, MaximumEntries - candidateOffset)).ToArray();
            if (ids.Length == 0) break;
            using var books = await ReadRemoteJsonAsync(instance,
                $"ajax/books/{library}?ids={string.Join(',', ids)}&category_urls=false", token);
            foreach (var bookId in ids)
            {
                var id = bookId.ToString(CultureInfo.InvariantCulture);
                if (!books.RootElement.TryGetProperty(id, out var metadata) || metadata.ValueKind != JsonValueKind.Object) continue;
                var formats = RemoteFormats(metadata);
                var ebook = TextFormats.FirstOrDefault(formats.Contains);
                var comic = ComicFormats.FirstOrDefault(formats.Contains);
                foreach (var format in new[] { ebook, comic }.OfType<string>())
                {
                    var itemId = CategoryItemId(instance, id, format, ebook is not null && comic is not null);
                    var authors = metadata.TryGetProperty("authors", out var authorList) && authorList.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", authorList.EnumerateArray().Select(author => author.GetString())) : "";
                    items.Add(new LibraryItem(itemId, JsonText(metadata, "title") ?? $"Book {id}", authors,
                        ItemUrl(instance.Id, itemId), new Uri($"/libraries/{instance.Id:D}/covers/{id}", UriKind.Relative),
                        ComicFormats.Contains(format) ? LibraryMediaType.Comic : LibraryMediaType.TextEbook,
                        Description: NormalizeDescription(JsonText(metadata, "comments") ?? ""),
                        CreatedAt: JsonDate(metadata, "timestamp"), UpdatedAt: JsonDate(metadata, "last_modified"),
                        ReaderUrl: instance.GetCalibreReaderUrl(id, format)) { FileFormat = format });
                }
            }
            candidateOffset += ids.Length;
            if (sort != LibrarySort.Random && items.Count >= offset + limit) break;
            if (candidateOffset >= search.RootElement.GetProperty("total_num").GetInt32()) break;
        }
        return (sort == LibrarySort.Random ? LibrarySearchService.SortItems(items, sort) : items).Skip(offset).Take(limit).ToArray();
    }

    private static HashSet<string> RemoteFormats(JsonElement metadata) =>
        metadata.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array
            ? formats.EnumerateArray().Select(format => format.GetString()?.ToUpperInvariant() ?? "").ToHashSet(StringComparer.Ordinal)
            : [];

    private static string? JsonText(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? JsonDate(JsonElement metadata, string name) =>
        DateTimeOffset.TryParse(JsonText(metadata, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private async Task<LocalLibraryFile?> OpenRemoteItemAsync(LocalLibraryInstance instance, string itemId, CancellationToken token)
    {
        if (!TryItemId(itemId, out var bookId, out var requestedFormat)) return null;
        using var book = await ReadRemoteJsonAsync(instance,
            $"ajax/book/{bookId}/{Uri.EscapeDataString(instance.ContentServerLibraryId!)}?category_urls=false", token);
        var formats = RemoteFormats(book.RootElement);
        var preferred = instance.Kind == LocalLibraryKind.CalibreComic ? ComicFormats.Concat(TextFormats) : TextFormats.Concat(ComicFormats);
        var format = requestedFormat ?? preferred.FirstOrDefault(formats.Contains);
        if (format is null || !formats.Contains(format)) return null;
        return await OpenRemoteFileAsync(instance, bookId, format, token);
    }

    private async Task<LocalLibraryFile?> OpenRemoteFileAsync(LocalLibraryInstance instance, long bookId, string format, CancellationToken token)
    {
        var response = await _http.GetAsync(RemoteUrl(instance,
            $"get/{format.ToLowerInvariant()}/{bookId}/{Uri.EscapeDataString(instance.ContentServerLibraryId!)}"),
            HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(token);
            if (format == "cover")
            {
                await using var cover = stream;
                var bytes = await ReadBoundedAsync(cover, 5 * 1024 * 1024, token);
                response.Dispose();
                return bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
                    ? new LocalLibraryFile(new MemoryStream(bytes), "image/jpeg", null) : null;
            }
            return new LocalLibraryFile(new ResponseStream(stream, response), format == "cover" ? "image/jpeg" : ContentType(format),
                format == "cover" ? null : $"book-{bookId}.{format.ToLowerInvariant()}");
        }
        catch { response.Dispose(); throw; }
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private static async Task<IReadOnlyList<LibraryItem>> SearchNotesAsync(LocalLibraryInstance instance, string root,
        string query, int limit, CancellationToken cancellationToken, int offset, LibrarySort sort)
    {
        // Without a sort, stop at the first page; otherwise every match must be collected before ordering.
        var stopAt = sort == LibrarySort.Default ? offset + limit : int.MaxValue;
        var items = new List<LibraryItem>();
        long scannedBytes = 0;
        foreach (var path in EnumerateNotes(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileInfo = new FileInfo(path);
                var length = fileInfo.Length;
                if (length > MaximumNoteBytes) continue;
                scannedBytes += length;
                if (scannedBytes > MaximumScanBytes) break;
                var text = await ReadNoteAsync(root, path, cancellationToken);
                if (text is null || (!Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !text.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
                var id = NoteId(root, path);
                items.Add(new LibraryItem(id, Path.GetFileName(path), "Note", ItemUrl(instance.Id, id),
                    MediaType: LibraryMediaType.Note, CreatedAt: fileInfo.CreationTimeUtc,
                    UpdatedAt: fileInfo.LastWriteTimeUtc, Path: Path.GetRelativePath(root, path),
                    ReaderUrl: new Uri($"/libraries/{instance.Id:D}/read/{id}", UriKind.Relative))
                {
                    FileFormat = Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
                });
                if (items.Count == stopAt) break;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return LibrarySearchService.SortItems(items, sort).Skip(offset).Take(limit).ToArray();
    }

    private static IEnumerable<string> EnumerateNotes(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] children;
            try
            {
                LocalLibraryPaths.Root(root);
                if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase))
                    LocalLibraryPaths.Resolve(root, Path.GetRelativePath(root, directory));
                children = Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    RecurseSubdirectories = false
                }).Take(MaximumEntries - entries + 1).Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries) yield break;
                string path;
                FileAttributes attributes;
                try
                {
                    path = LocalLibraryPaths.Resolve(root, Path.GetRelativePath(root, child));
                    attributes = File.GetAttributes(path);
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (NoteExtensions.Contains(Path.GetExtension(path))) yield return path;
            }
        }
    }

    private static async Task<string?> ReadNoteAsync(string root, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = OpenRead(root, path);
            if (stream.Length > MaximumNoteBytes) return null;
            var bytes = await ReadBoundedAsync(stream, MaximumNoteBytes, cancellationToken);
            var text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            return text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ? null : text;
        }
        catch (Exception exception) when (IsFileFailure(exception) || exception is DecoderFallbackException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<LibraryItem>> SearchCalibreAsync(LocalLibraryInstance instance, string root,
        string query, int limit, CancellationToken cancellationToken, int offset, LibrarySort sort)
    {
        await using var database = await OpenDatabaseAsync(root, cancellationToken);
        var connection = database.Connection;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(books)";
            await using var schemaReader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await schemaReader.ReadAsync(cancellationToken)) columns.Add(schemaReader.GetString(1));
        }
        var created = columns.Contains("timestamp") ? "b.timestamp" : "NULL";
        var orderBy = sort switch
        {
            LibrarySort.TitleAscending => "b.title COLLATE NOCASE ASC, b.id",
            LibrarySort.TitleDescending => "b.title COLLATE NOCASE DESC, b.id",
            LibrarySort.DateNewest => $"{created} IS NULL, {created} DESC, b.id",
            LibrarySort.DateOldest => $"{created} IS NULL, {created} ASC, b.id",
            LibrarySort.Random => "random()",
            _ => "b.id"
        };
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT b.id, b.title, b.path,
                COALESCE((SELECT group_concat(a.name, ', ') FROM authors a
                    JOIN books_authors_link ba ON ba.author = a.id WHERE ba.book = b.id), ''),
                (SELECT substr(c.text, 1, $maximumCommentLength) FROM comments c WHERE c.book = b.id LIMIT 1),
                {{created}},
                {{(columns.Contains("last_modified") ? "b.last_modified" : "NULL")}}
            FROM books b
            WHERE b.title LIKE $query ESCAPE '\'
                OR EXISTS (SELECT 1 FROM authors a JOIN books_authors_link ba ON ba.author = a.id
                    WHERE ba.book = b.id AND a.name LIKE $query ESCAPE '\')
                OR EXISTS (SELECT 1 FROM tags t JOIN books_tags_link bt ON bt.tag = t.id
                    WHERE bt.book = b.id AND t.name LIKE $query ESCAPE '\')
                OR EXISTS (SELECT 1 FROM comments c WHERE c.book = b.id AND c.text LIKE $query ESCAPE '\')
            ORDER BY {{orderBy}}
            LIMIT $maximumEntries
            """;
        command.Parameters.AddWithValue("$maximumEntries", MaximumEntries);
        command.Parameters.AddWithValue("$maximumCommentLength", MaximumCommentLength);
        command.Parameters.AddWithValue("$query", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        using var cancellation = cancellationToken.Register(command.Cancel);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<LibraryItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bookId = reader.GetInt64(0);
            if (bookId <= 0) continue;
            var bookPath = reader.GetString(2);
            var ebook = await FindFormatAsync(connection, root, bookPath, bookId, LocalLibraryKind.CalibreText, cancellationToken);
            var comic = await FindFormatAsync(connection, root, bookPath, bookId, LocalLibraryKind.CalibreComic, cancellationToken);
            if (ebook is null && comic is null) continue;
            var id = bookId.ToString(CultureInfo.InvariantCulture);
            Uri? coverUrl = null;
            try
            {
                var coverPath = LocalLibraryPaths.Resolve(root, bookPath, "cover.jpg");
                if (new FileInfo(coverPath).Length <= 5 * 1024 * 1024)
                    coverUrl = new Uri($"/libraries/{instance.Id:D}/covers/{id}", UriKind.Relative);
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
            }

            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            foreach (var file in new[] { ebook, comic }.Where(file => file is not null))
            {
                if (offset > 0) { offset--; continue; }
                var comicItem = ComicFormats.Contains(file!.Value.Format);
                var itemId = CategoryItemId(instance, id, file.Value.Format, ebook is not null && comic is not null);
                items.Add(new LibraryItem(itemId, string.IsNullOrWhiteSpace(title) ? bookPath : title, reader.GetString(3), ItemUrl(instance.Id, itemId), coverUrl,
                comicItem ? LibraryMediaType.Comic : LibraryMediaType.TextEbook,
                Description: reader.IsDBNull(4) ? null : NormalizeDescription(reader.GetString(4)),
                CreatedAt: ReadCalibreDate(reader, 5), UpdatedAt: ReadCalibreDate(reader, 6), Path: bookPath,
                ReaderUrl: instance.GetCalibreReaderUrl(id, file.Value.Format)) { FileFormat = file.Value.Format });
                if (items.Count == limit) break;
            }
            if (items.Count == limit) break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return items;
    }

    private static DateTimeOffset? ReadCalibreDate(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && DateTimeOffset.TryParse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static string? NormalizeDescription(string comment)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        var timeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var text = Regex.Replace(comment, @"<!--.*?(?:-->|\z)", "", options, timeout);
            foreach (var tag in new[] { "script", "style" })
                text = Regex.Replace(text, $@"<\s*{tag}\b[^>]*>.*?(?:</\s*{tag}\s*>|\z)", "", options, timeout);
            text = Regex.Replace(text, @"<(?:""[^""]*""|'[^']*'|[^'""<>])*>", match =>
                Regex.IsMatch(match.Value, @"^<\s*/?\s*(?:p|div|li|ul|ol|blockquote|h[1-6]|tr|section)\b", options, timeout)
                    ? "\n\n"
                    : Regex.IsMatch(match.Value, @"^<\s*br\b", options, timeout) ? "\n" : "",
                options, timeout);
            text = WebUtility.HtmlDecode(text);
            var clean = new StringBuilder(text.Length);
            foreach (var character in text)
                if (!char.IsControl(character) || character is '\r' or '\n' or '\t') clean.Append(character);
            text = Regex.Replace(clean.ToString(), @"\r\n?", "\n", options, timeout);
            text = Regex.Replace(text, @"[^\S\n]+", " ", options, timeout);
            text = Regex.Replace(text, @" *\n *", "\n", options, timeout);
            text = Regex.Replace(text, @"\n{3,}", "\n\n", options, timeout).Trim();
            if (text.Length > MaximumDescriptionLength)
            {
                var length = char.IsHighSurrogate(text[MaximumDescriptionLength - 1])
                    ? MaximumDescriptionLength - 1 : MaximumDescriptionLength;
                text = text[..length].TrimEnd();
            }
            return text.Length == 0 ? null : text;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static async Task<DatabaseRead> OpenDatabaseAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = LocalLibraryPaths.Resolve(root, "metadata.db");
        var guard = LocalLibraryPaths.OpenRead(root, databasePath, FileShare.ReadWrite);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(databasePath).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            LocalLibraryPaths.Resolve(root, "metadata.db");
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new DatabaseRead(connection, guard);
        }
        catch
        {
            await connection.DisposeAsync();
            await guard.DisposeAsync();
            throw;
        }
    }

    private sealed record DatabaseRead(SqliteConnection Connection, FileStream Guard) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Guard.DisposeAsync();
        }
    }

    private static async Task<(string Path, string Format)?> FindFormatAsync(SqliteConnection connection, string root,
        string bookPath, long bookId, LocalLibraryKind kind, CancellationToken cancellationToken, string? requestedFormat = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, format FROM data WHERE book = $id LIMIT 100";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var formats = kind == LocalLibraryKind.CalibreComic ? ComicFormats : TextFormats;
        (string Path, string Format)? selected = null;
        var bestRank = int.MaxValue;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = reader.GetString(0);
            var format = reader.GetString(1).ToUpperInvariant();
            if (requestedFormat is not null && format != requestedFormat) continue;
            var rank = Array.IndexOf(formats, format);
            if (rank < 0 || rank >= bestRank || !IsFileStem(name)) continue;
            try
            {
                var path = LocalLibraryPaths.Resolve(root, bookPath, name + "." + format.ToLowerInvariant());
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) continue;
                selected = (path, format);
                bestRank = rank;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
            }
        }

        return selected;
    }

    private static bool IsFileStem(string value) => !string.IsNullOrWhiteSpace(value)
        && value is not ("." or "..") && !value.Any(char.IsControl)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.EndsWith(' ') && !value.EndsWith('.');

    private static FileStream OpenRead(string root, string path)
        => LocalLibraryPaths.OpenRead(root, path);

    private static async Task<LocalLibraryFile?> ReadCoverAsync(string root, string bookPath, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(root, LocalLibraryPaths.Resolve(root, bookPath, "cover.jpg"));
        var bytes = await ReadBoundedAsync(stream, 5 * 1024 * 1024, cancellationToken);
        if (bytes.Length < 3 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff) return null;
        return new LocalLibraryFile(new MemoryStream(bytes), "image/jpeg", null);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1L - buffer.Length)), cancellationToken);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
            if (buffer.Length > maximumBytes) throw new InvalidDataException("The library item exceeds the size limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    private static Uri ItemUrl(Guid instanceId, string id) => new($"/libraries/{instanceId:D}/items/{id}", UriKind.Relative);
    private static string CategoryItemId(LocalLibraryInstance instance, string id, string format, bool mixed) =>
        mixed && (ComicFormats.Contains(format) != (instance.Kind == LocalLibraryKind.CalibreComic)) ? $"{id}:{format}" : id;
    private static string NoteId(string root, string path) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path))));
    private static bool TryBookId(string value, out long id) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out id) && id > 0;
    private static bool IsFileFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;

    private static string ContentType(string format) => format switch
    {
        "EPUB" => "application/epub+zip",
        "PDF" => "application/pdf",
        "CBZ" => "application/vnd.comicbook+zip",
        "CBR" => "application/vnd.comicbook-rar",
        "MD" or "MARKDOWN" => "text/markdown; charset=utf-8",
        "TXT" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}