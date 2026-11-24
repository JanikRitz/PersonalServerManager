using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PersonalServerManager.Covers;

public sealed record PromptImage(byte[] Bytes, string ContentType);

/// <summary>Extracts bounded prompt context (opening lines, first comic page) from book files.</summary>
public static class BookContextReader
{
    public const int MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumExcerptCharacters = 24000;
    private const int MaximumTextBytes = 4 * 1024 * 1024;
    private const int MaximumEpubDocuments = 300;
    private const long MaximumEpubBytes = 32 * 1024 * 1024;
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    public static bool SupportsText(string? format) => format?.ToUpperInvariant() is "EPUB" or "TXT" or "MD" or "MARKDOWN";
    public static bool SupportsImage(string? format) => format?.ToUpperInvariant() is "CBZ";

    public static async Task<string?> ReadLinesAsync(Stream stream, string format, int lineCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (lineCount < 1) throw new ArgumentOutOfRangeException(nameof(lineCount));
        if (!SupportsText(format)) return null;
        var lines = new List<string>();
        var characters = 0;
        bool Add(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (characters + line.Length > MaximumExcerptCharacters) line = line[..Math.Max(0, MaximumExcerptCharacters - characters)];
                if (line.Length == 0) return false;
                lines.Add(line);
                characters += line.Length;
                if (lines.Count >= lineCount || characters >= MaximumExcerptCharacters) return false;
            }
            return true;
        }

        if (!format.Equals("EPUB", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await ReadBoundedAsync(stream, MaximumTextBytes, true, cancellationToken);
            Add(Clean(Encoding.UTF8.GetString(bytes)));
        }
        else
        {
            await using var seekable = await SeekableAsync(stream, cancellationToken);
            using var archive = new ZipArchive(seekable, ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in EpubDocuments(archive).Take(MaximumEpubDocuments))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaximumTextBytes || (total += entry.Length) > MaximumEpubBytes) break;
                await using var content = entry.Open();
                var html = Encoding.UTF8.GetString(await ReadBoundedAsync(content, MaximumTextBytes, false, cancellationToken));
                if (!Add(HtmlToText(html))) break;
            }
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    public static async Task<PromptImage?> ReadFirstComicImageAsync(Stream stream, string format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!SupportsImage(format)) return null;
        await using var seekable = await SeekableAsync(stream, cancellationToken);
        using var archive = new ZipArchive(seekable, ZipArchiveMode.Read);
        var entry = archive.Entries
            .Where(item => item.Length > 0 && !item.FullName.EndsWith('/') &&
                !item.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(item.FullName).StartsWith('.') &&
                Path.GetExtension(item.FullName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")
            .OrderBy(item => item.FullName, NaturalComparer.Instance)
            .FirstOrDefault();
        if (entry is null || entry.Length > MaximumImageBytes) return null;
        await using var content = entry.Open();
        var bytes = await ReadBoundedAsync(content, MaximumImageBytes, false, cancellationToken);
        var type = ImageType(bytes);
        return type is null ? null : new PromptImage(bytes, type);
    }

    public static string? ImageType(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xff, 0xd8, 0xff, ..] => "image/jpeg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null
    };

    private static IEnumerable<ZipArchiveEntry> EpubDocuments(ZipArchive archive)
    {
        var spine = ReadSpine(archive);
        if (spine is { Count: > 0 }) return spine;
        return archive.Entries
            .Where(entry => Path.GetExtension(entry.FullName).ToLowerInvariant() is ".xhtml" or ".html" or ".htm")
            .OrderBy(entry => entry.FullName, NaturalComparer.Instance);
    }

    private static List<ZipArchiveEntry>? ReadSpine(ZipArchive archive)
    {
        try
        {
            var container = Load(archive.GetEntry("META-INF/container.xml"));
            var opfPath = container?.SelectSingleNode("//*[local-name()='rootfile']/@full-path")?.Value;
            if (string.IsNullOrWhiteSpace(opfPath)) return null;
            var opf = Load(archive.GetEntry(opfPath));
            if (opf is null) return null;
            var baseDirectory = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
            var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode item in opf.SelectNodes("//*[local-name()='manifest']/*[local-name()='item']")!)
                if (item.Attributes?["id"]?.Value is { } id && item.Attributes["href"]?.Value is { } href)
                    manifest.TryAdd(id, href);
            var entries = new List<ZipArchiveEntry>();
            foreach (XmlNode itemRef in opf.SelectNodes("//*[local-name()='spine']/*[local-name()='itemref']")!)
            {
                if (itemRef.Attributes?["idref"]?.Value is not { } idRef || !manifest.TryGetValue(idRef, out var href)) continue;
                var path = ResolvePath(baseDirectory, Uri.UnescapeDataString(href.Split('#')[0]));
                if (path is not null && archive.GetEntry(path) is { } entry) entries.Add(entry);
            }
            return entries;
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static XmlDocument? Load(ZipArchiveEntry? entry)
    {
        if (entry is null || entry.Length > 1024 * 1024) return null;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024
        });
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }

    private static string? ResolvePath(string baseDirectory, string href)
    {
        var parts = new List<string>();
        foreach (var part in (baseDirectory + href).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string HtmlToText(string html)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        try
        {
            var text = Regex.Replace(html, @"<!--.*?(?:-->|\z)", "", options, RegexTimeout);
            text = Regex.Replace(text, @"<\s*(head|script|style)\b[^>]*>.*?(?:<\s*/\s*\1\s*>|\z)", "", options, RegexTimeout);
            text = Regex.Replace(text, @"<\s*/?\s*(?:p|div|br|li|h[1-6]|tr|blockquote|section)\b[^>]*>", "\n", options, RegexTimeout);
            text = Regex.Replace(text, @"<[^>]*>", "", options, RegexTimeout);
            return Clean(WebUtility.HtmlDecode(text));
        }
        catch (RegexMatchTimeoutException)
        {
            return "";
        }
    }

    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var character in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (character == '\n') clean.Append('\n');
            else if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
            }
            else clean.Append(character);
        }
        return clean.ToString();
    }

    private static async Task<Stream> SeekableAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek) return stream;
        var temporary = new FileStream(Path.Combine(Path.GetTempPath(), $"psm-book-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (temporary.Length + read > MaximumArchiveBytes) throw new InvalidDataException("The book file is too large.");
                await temporary.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            temporary.Position = 0;
            return temporary;
        }
        catch
        {
            await temporary.DisposeAsync();
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, bool truncate, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1L - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length <= maximumBytes) continue;
            if (!truncate) throw new InvalidDataException("The book content exceeds the size limit.");
            buffer.SetLength(maximumBytes);
            break;
        }
        return buffer.ToArray();
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var startX = i; var startY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var numberX = x[startX..i].TrimStart('0');
                    var numberY = y[startY..j].TrimStart('0');
                    var result = numberX.Length != numberY.Length ? numberX.Length.CompareTo(numberY.Length)
                        : string.CompareOrdinal(numberX, numberY);
                    if (result != 0) return result;
                }
                else
                {
                    var result = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (result != 0) return result;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
