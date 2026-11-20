using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PersonalServerManager.Search;

namespace PersonalServerManager.LocalLibraries;

public sealed record LocalLibraryFile(Stream Content, string ContentType, string? FileName) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed class LocalLibraryClient
{
    private const int MaximumNoteBytes = 1024 * 1024;
    private const int MaximumScanBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private const int MaximumDescriptionLength = 4000;
    private const int MaximumCommentLength = 32768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> NoteExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt" };
    private static readonly string[] TextFormats = ["EPUB", "PDF", "AZW3", "MOBI", "AZW", "FB2", "TXT", "RTF", "DOCX", "ODT", "DJVU"];
    private static readonly string[] ComicFormats = ["CBZ", "CBR", "CB7", "CBT", "PDF", "EPUB"];

    public async Task<IReadOnlyList<LibraryItem>> SearchAsync(LocalLibraryInstance instance, string query, int limit,
        CancellationToken cancellationToken = default, int offset = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset is < 0 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(offset));
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 1024 || query.Any(char.IsControl)) throw new ArgumentException("Invalid search query.", nameof(query));
        if (!Enum.IsDefined(instance.Kind)) throw new InvalidDataException("Invalid library kind.");
        if (string.IsNullOrWhiteSpace(query)) return [];
        var root = LocalLibraryPaths.Root(instance.RootPath);
        if (instance.Kind == LocalLibraryKind.Notes)
            return await SearchNotesAsync(instance, root, query.Trim(), limit, cancellationToken, offset);
        try
        {
            return await SearchCalibreAsync(instance, root, query.Trim(), limit, cancellationToken, offset);
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

        if (instance.Kind is not (LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic)
            || !TryBookId(itemId, out var bookId)) return null;
        await using var database = await OpenDatabaseAsync(root, cancellationToken);
        var connection = database.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, path FROM books WHERE id = $id";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var bookPath = reader.GetString(2);
        var file = await FindFormatAsync(connection, root, bookPath, bookId, instance.Kind, cancellationToken);
        if (file is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return new LocalLibraryFile(OpenRead(root, file.Value.Path), ContentType(file.Value.Format), $"book-{bookId}.{file.Value.Format.ToLowerInvariant()}");
    }

    public async Task<LocalLibraryFile?> OpenCoverAsync(LocalLibraryInstance instance, string bookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instance.Kind is not (LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic)
            || !TryBookId(bookId, out var numericId)) return null;
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

    private static async Task<IReadOnlyList<LibraryItem>> SearchNotesAsync(LocalLibraryInstance instance, string root,
        string query, int limit, CancellationToken cancellationToken, int offset)
    {
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
                if (offset > 0)
                {
                    offset--;
                    continue;
                }
                var id = NoteId(root, path);
                items.Add(new LibraryItem(id, Path.GetFileName(path), "Note", ItemUrl(instance.Id, id),
                    MediaType: LibraryMediaType.Note, CreatedAt: fileInfo.CreationTimeUtc,
                    UpdatedAt: fileInfo.LastWriteTimeUtc, Path: Path.GetRelativePath(root, path)));
                if (items.Count == limit) break;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return items;
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
        string query, int limit, CancellationToken cancellationToken, int offset)
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
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT b.id, b.title, b.path,
                COALESCE((SELECT group_concat(a.name, ', ') FROM authors a
                    JOIN books_authors_link ba ON ba.author = a.id WHERE ba.book = b.id), ''),
                (SELECT substr(c.text, 1, $maximumCommentLength) FROM comments c WHERE c.book = b.id LIMIT 1),
                {{(columns.Contains("timestamp") ? "b.timestamp" : "NULL")}},
                {{(columns.Contains("last_modified") ? "b.last_modified" : "NULL")}}
            FROM books b
            WHERE b.title LIKE $query ESCAPE '\'
                OR EXISTS (SELECT 1 FROM authors a JOIN books_authors_link ba ON ba.author = a.id
                    WHERE ba.book = b.id AND a.name LIKE $query ESCAPE '\')
                OR EXISTS (SELECT 1 FROM tags t JOIN books_tags_link bt ON bt.tag = t.id
                    WHERE bt.book = b.id AND t.name LIKE $query ESCAPE '\')
                OR EXISTS (SELECT 1 FROM comments c WHERE c.book = b.id AND c.text LIKE $query ESCAPE '\')
            ORDER BY b.id
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
            var file = await FindFormatAsync(connection, root, bookPath, bookId, instance.Kind, cancellationToken);
            if (file is null) continue;
            if (offset > 0)
            {
                offset--;
                continue;
            }
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
            items.Add(new LibraryItem(id, string.IsNullOrWhiteSpace(title) ? bookPath : title, reader.GetString(3), ItemUrl(instance.Id, id), coverUrl,
                instance.Kind == LocalLibraryKind.CalibreComic ? LibraryMediaType.Comic : LibraryMediaType.TextEbook,
                Description: reader.IsDBNull(4) ? null : NormalizeDescription(reader.GetString(4)),
                CreatedAt: ReadCalibreDate(reader, 5), UpdatedAt: ReadCalibreDate(reader, 6), Path: bookPath));
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
        string bookPath, long bookId, LocalLibraryKind kind, CancellationToken cancellationToken)
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
        "TXT" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}