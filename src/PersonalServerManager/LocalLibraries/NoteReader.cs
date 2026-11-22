using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Search;
using PersonalServerManager.Stash;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PersonalServerManager.LocalLibraries;

/// <summary>Renders a Foam-style note as a standalone, script-free HTML page.</summary>
public static class NoteReader
{
    private const int MaximumEmbedDepth = 3;
    private const int MaximumEmbeds = 50;
    private const long MaximumBacklinkScanBytes = 32 * 1024 * 1024;
    private static readonly string[] NoteExtensions = [".md", ".markdown", ".txt"];
    private static readonly Regex WikiLinkPattern = new(@"\[\[([^\[\]\r\n]{1,500})\]\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex MarkdownLinkPattern = new(@"\]\(\s*<?([^)\s>]{1,1000})>?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SchemePattern = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagPattern = new(@"(<[^>]*>)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex HtmlReferencePattern = new(
        @"!?\[\[[^\[\]\r\n]{1,500}\]\]|@(?:hydrusPreview|hydrus|stashImage|stashVideo|stashPerformer|stashMarker)\{[^}\r\n]{1,300}\}|!?\[[^\[\]\r\n]{0,500}\]\([^)\r\n]{1,1000}\)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Use<NoteSyntaxExtension>()
        .Build();

    public static async Task<string?> RenderPageAsync(LocalLibraryInstance instance, string noteId,
        HydrusInstance? hydrus, StashInstance? stash, CancellationToken cancellationToken = default)
    {
        if (instance.Kind != LocalLibraryKind.Notes || noteId.Length != 64 || !noteId.All(char.IsAsciiHexDigit)) return null;
        var vault = LocalLibraryClient.GetNoteVault(instance, cancellationToken);
        var notePath = vault.Notes.FirstOrDefault(path => LocalLibraryClient.NoteId(vault, path).Equals(noteId, StringComparison.Ordinal));
        if (notePath is null) return null;
        var text = await LocalLibraryClient.ReadNoteTextAsync(vault, notePath, cancellationToken);
        if (text is null) return null;

        var context = new Context(instance, vault, hydrus, stash);
        var document = Markdown.Parse(text, Pipeline);
        await PrepareAsync(document, notePath, context, 0, [notePath], cancellationToken);
        var body = Sanitize(Render(document.ToList()), notePath, context);
        var headings = document.Descendants<HeadingBlock>().Where(heading => heading.Level <= 4)
            .Select(heading => (heading.Level, Id: heading.GetAttributes().Id, Text: PlainText(heading)))
            .Where(heading => !string.IsNullOrEmpty(heading.Id) && heading.Text.Length > 0).ToList();
        var frontMatter = document.OfType<YamlFrontMatterBlock>().FirstOrDefault();
        var yaml = frontMatter is null ? null : ParseFrontMatter(frontMatter);
        var firstHeading = headings.FirstOrDefault(heading => heading.Level == 1).Text;
        var title = yaml?.Title ?? (string.IsNullOrEmpty(firstHeading)
            ? Path.GetFileNameWithoutExtension(notePath) : WebUtility.HtmlDecode(firstHeading));
        var backlinks = await FindBacklinksAsync(notePath, context, cancellationToken);

        var page = new StringBuilder();
        page.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="referrer" content="no-referrer"><title>
            """).Append(Encode(title)).Append("</title><style>").Append(Styles).Append("</style></head><body><div class=\"layout\"><aside id=\"note-panel\">");
        page.Append("<nav class=\"actions\" aria-label=\"Note actions\"><a href=\"/\">Home</a><a href=\"")
            .Append(Encode($"/libraries/{instance.Id:D}/items/{noteId}")).Append("\">Original note</a></nav>");
        page.Append("<p class=\"note-path\">").Append(Encode(instance.Name)).Append(" / ").Append(Encode(notePath.Replace('\\', '/'))).Append("</p>");
        if (yaml is not null)
            page.Append("<details open class=\"panel\"><summary>Properties</summary>").Append(yaml.Html).Append("</details>");
        if (headings.Count > 0)
        {
            var minimum = headings.Min(heading => heading.Level);
            page.Append("<details open class=\"panel\"><summary>Contents</summary><nav class=\"toc\" aria-label=\"Table of contents\"><ol>");
            foreach (var heading in headings)
                page.Append("<li class=\"toc-level-").Append(heading.Level - minimum).Append("\"><a href=\"#")
                    .Append(Encode(heading.Id!)).Append("\">").Append(heading.Text).Append("</a></li>");
            page.Append("</ol></nav></details>");
        }
        page.Append("<details open class=\"panel\"><summary>Backlinks</summary>");
        if (backlinks.Count == 0) page.Append("<p class=\"empty\">No notes link here.</p>");
        else
        {
            page.Append("<ul class=\"backlinks\">");
            foreach (var path in backlinks)
                page.Append("<li><a href=\"").Append(Encode(context.ReaderUrl(path, null))).Append("\" title=\"")
                    .Append(Encode(path.Replace('\\', '/'))).Append("\">").Append(Encode(Path.GetFileNameWithoutExtension(path))).Append("</a></li>");
            page.Append("</ul>");
        }
        page.Append("</details></aside><main><nav class=\"jump\"><a href=\"#note-panel\">Properties, contents and backlinks</a></nav><article>")
            .Append(body).Append("</article></main></div></body></html>");
        return page.ToString();
    }

    private static async Task PrepareAsync(MarkdownDocument document, string notePath, Context context, int depth,
        HashSet<string> chain, CancellationToken cancellationToken)
    {
        foreach (var link in document.Descendants<WikiLinkInline>()) ResolveWikiLink(link, notePath, context);
        foreach (var token in document.Descendants<MediaTokenInline>()) ResolveMediaToken(token, context);
        foreach (var link in document.Descendants<LinkInline>()) RewriteLink(link, notePath, context);
        foreach (var link in document.Descendants<AutolinkInline>())
            if (!IsSafeAbsolute(link.Url)) link.Url = "#";

        foreach (var paragraph in document.Descendants<ParagraphBlock>().ToList())
        {
            if (paragraph.Inline is null || paragraph.Parent is not { } parent) continue;
            var embeds = new List<WikiLinkInline>();
            var onlyEmbeds = true;
            foreach (var inline in paragraph.Inline)
            {
                if (inline is WikiLinkInline { IsEmbed: true } embed) embeds.Add(embed);
                else if (!(inline is LineBreakInline || inline is LiteralInline literal && literal.Content.IsEmptyOrWhitespace()))
                {
                    onlyEmbeds = false;
                    break;
                }
            }
            if (!onlyEmbeds || !embeds.Any(embed => embed.NotePath is not null)) continue;

            var index = parent.IndexOf(paragraph);
            parent.RemoveAt(index);
            foreach (var embed in embeds)
            {
                var html = embed.NotePath is null
                    ? "<p>" + RenderInline(embed) + "</p>"
                    : await RenderEmbedAsync(embed, context, depth, chain, cancellationToken);
                parent.Insert(index++, new RawHtmlBlock(html));
            }
        }

        foreach (var block in document.Descendants<HtmlBlock>().ToList())
        {
            if (block.Type == HtmlBlockType.Comment || block.Parent is not { } parent) continue;
            var html = await ExpandHtmlReferencesAsync(block.Lines.ToString(), notePath, context, depth, chain, cancellationToken);
            var index = parent.IndexOf(block);
            parent.RemoveAt(index);
            parent.Insert(index, new RawHtmlBlock(html));
        }
    }

    // Raw HTML blocks are not parsed as Markdown, so references in their text are rendered one by one.
    private static async Task<string> ExpandHtmlReferencesAsync(string html, string notePath, Context context, int depth,
        HashSet<string> chain, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(html.Length);
        foreach (var segment in HtmlTagPattern.Split(html))
        {
            if (segment.StartsWith('<'))
            {
                result.Append(segment);
                continue;
            }
            var position = 0;
            foreach (Match match in HtmlReferencePattern.Matches(segment))
            {
                result.Append(segment, position, match.Index - position);
                var document = Markdown.Parse(match.Value, Pipeline);
                await PrepareAsync(document, notePath, context, depth, chain, cancellationToken);
                var rendered = Render(document.ToList()).Trim();
                if (rendered.StartsWith("<p>", StringComparison.Ordinal) && rendered.EndsWith("</p>", StringComparison.Ordinal))
                    rendered = rendered[3..^4];
                result.Append(rendered);
                position = match.Index + match.Length;
            }
            result.Append(segment, position, segment.Length - position);
        }
        return result.ToString();
    }

    private static string Sanitize(string html, string notePath, Context context)
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "details", "summary", "section", "figure", "figcaption", "mark", "kbd", "input" })
            sanitizer.AllowedTags.Add(tag);
        foreach (var attribute in new[] { "id", "class", "loading", "target", "rel", "open", "checked", "disabled", "type", "title", "alt" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.FilterUrl += (_, args) =>
        {
            if (args.SanitizedUrl is { } url &&
                RewriteUrl(url, args.Tag.TagName.Equals("img", StringComparison.OrdinalIgnoreCase), notePath, context) is { } rewritten)
                args.SanitizedUrl = rewritten;
        };
        return sanitizer.Sanitize(html);
    }

    private static async Task<string> RenderEmbedAsync(WikiLinkInline embed, Context context, int depth,
        HashSet<string> chain, CancellationToken cancellationToken)
    {
        var path = embed.NotePath!;
        var header = $"<div class=\"embed-title\"><a href=\"{Encode(embed.Href!)}\">{Encode(embed.Label)}</a></div>";
        string content;
        if (chain.Contains(path)) content = "<p class=\"empty\">Embedding skipped: the note embeds itself.</p>";
        else if (depth + 1 > MaximumEmbedDepth || context.EmbedCount >= MaximumEmbeds) content = "<p class=\"empty\">Embedding skipped: too many nested embeds.</p>";
        else if (await LocalLibraryClient.ReadNoteTextAsync(context.Vault, path, cancellationToken) is not { } text)
            content = "<p class=\"empty\">The embedded note could not be read.</p>";
        else
        {
            context.EmbedCount++;
            var document = Markdown.Parse(text, Pipeline);
            await PrepareAsync(document, path, context, depth + 1, [.. chain, path], cancellationToken);
            var blocks = document.ToList();
            if (embed.Heading is not null)
            {
                var slug = LinkHelper.UrilizeAsGfm(embed.Heading);
                var start = blocks.FindIndex(block => block is HeadingBlock heading && heading.GetAttributes().Id == slug);
                if (start < 0) blocks = [];
                else
                {
                    var level = ((HeadingBlock)blocks[start]).Level;
                    var end = blocks.FindIndex(start + 1, block => block is HeadingBlock heading && heading.Level <= level);
                    blocks = blocks.GetRange(start, (end < 0 ? blocks.Count : end) - start);
                }
            }
            content = blocks.Count == 0 ? "<p class=\"empty\">The embedded section was not found.</p>" : Sanitize(Render(blocks), path, context);
        }
        return $"<section class=\"embed\">{header}{content}</section>";
    }

    private static void ResolveWikiLink(WikiLinkInline link, string notePath, Context context)
    {
        if (link.Target.Length == 0)
        {
            if (link.Heading is not null) link.Href = "#" + LinkHelper.UrilizeAsGfm(link.Heading);
            return;
        }
        if (LocalLibraryClient.ImageExtensions.Contains(Path.GetExtension(link.Target)))
        {
            var image = context.ResolveWiki(link.Target, notePath, images: true);
            if (image is null) return;
            link.ImageSrc = context.FileUrl(image);
            link.Href = link.ImageSrc;
            return;
        }
        var note = context.ResolveWiki(link.Target, notePath, images: false);
        if (note is null) return;
        link.NotePath = note;
        link.Href = context.ReaderUrl(note, link.Heading);
    }

    private static void ResolveMediaToken(MediaTokenInline token, Context context)
    {
        var id = token.Id.ToString(CultureInfo.InvariantCulture);
        if (token.Name.StartsWith("hydrus", StringComparison.Ordinal))
        {
            if (context.Hydrus is not { } hydrus) return;
            var full = HydrusClientMedia.CreateLink(hydrus.Id, token.Id).ToString();
            token.Href = full;
            token.ImageSrc = token.Name == "hydrus" ? full : PreviewService.CreateLink("hydrus", hydrus.Id, "files", id)?.ToString();
            token.FullSize = token.Name == "hydrus";
            return;
        }
        if (context.Stash is not { } stash) return;
        var (category, itemId, webPath) = token.Name switch
        {
            "stashImage" => ("images", id, $"images/{id}"),
            "stashVideo" => ("scenes", id, $"scenes/{id}"),
            "stashPerformer" => ("performers", id, $"performers/{id}"),
            _ => ("markers", id, $"scenes/{token.SceneId.ToString(CultureInfo.InvariantCulture)}")
        };
        token.ImageSrc = PreviewService.CreateLink("stash", stash.Id, category, itemId)?.ToString();
        var browserUrl = stash.BrowserUrl(new Uri(new Uri(stash.WebUiUrl.AbsoluteUri.TrimEnd('/') + "/"), webPath));
        token.Href = browserUrl.IsAbsoluteUri ? browserUrl.AbsoluteUri : browserUrl.OriginalString;
    }

    private static void RewriteLink(LinkInline link, string notePath, Context context)
    {
        if (link.Url is { } url && RewriteUrl(url, link.IsImage, notePath, context) is { } rewritten) link.Url = rewritten;
    }

    private static string? RewriteUrl(string url, bool image, string notePath, Context context)
    {
        if (url.Length == 0 || url.StartsWith('#') || url.StartsWith("/libraries/", StringComparison.Ordinal)) return null;
        if (SchemePattern.IsMatch(url) || url.StartsWith("//", StringComparison.Ordinal))
            return IsSafeAbsolute(url) ? null : "#";
        var (path, fragment) = SplitUrl(url);
        if (path.Length == 0) return null;
        if (image || LocalLibraryClient.ImageExtensions.Contains(Path.GetExtension(path)))
            return context.ResolveRelative(notePath, path, images: true) is { } file ? context.FileUrl(file) : null;
        return context.ResolveRelative(notePath, path, images: false) is { } note
            ? context.ReaderUrl(note, null) + (fragment is null ? "" : "#" + fragment)
            : null;
    }

    private static async Task<IReadOnlyList<string>> FindBacklinksAsync(string notePath, Context context, CancellationToken cancellationToken)
    {
        var backlinks = new List<string>();
        long scanned = 0;
        foreach (var path in context.Vault.Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Equals(notePath, StringComparison.OrdinalIgnoreCase)) continue;
            var text = await LocalLibraryClient.ReadNoteTextAsync(context.Vault, path, cancellationToken);
            if (text is null) continue;
            scanned += text.Length;
            if (scanned > MaximumBacklinkScanBytes) break;
            try
            {
                var links = WikiLinkPattern.Matches(text).Select(match => ParseWikiTarget(match.Groups[1].Value).Target)
                    .Where(target => target.Length > 0 && !LocalLibraryClient.ImageExtensions.Contains(Path.GetExtension(target)))
                    .Select(target => context.ResolveWiki(target, path, images: false))
                    .Concat(MarkdownLinkPattern.Matches(text).Select(match => match.Groups[1].Value)
                        .Where(url => !url.StartsWith('#') && !SchemePattern.IsMatch(url))
                        .Select(url => context.ResolveRelative(path, SplitUrl(url).Path, images: false)));
                if (links.Any(target => notePath.Equals(target, StringComparison.OrdinalIgnoreCase))) backlinks.Add(path);
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return backlinks.OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static (string Target, string? Heading, string? Alias) ParseWikiTarget(string inner)
    {
        string? alias = null;
        var pipe = inner.IndexOf('|');
        if (pipe >= 0)
        {
            alias = inner[(pipe + 1)..].Trim();
            inner = inner[..pipe].TrimEnd('\\');
        }
        string? heading = null;
        var hash = inner.IndexOf('#');
        if (hash >= 0)
        {
            heading = inner[(hash + 1)..].Trim();
            inner = inner[..hash];
        }
        return (inner.Trim(), string.IsNullOrEmpty(heading) ? null : heading, string.IsNullOrEmpty(alias) ? null : alias);
    }

    private static (string Path, string? Fragment) SplitUrl(string url)
    {
        string? fragment = null;
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }
        var query = url.IndexOf('?');
        if (query >= 0) url = url[..query];
        try
        {
            return (Uri.UnescapeDataString(url), fragment);
        }
        catch (UriFormatException)
        {
            return ("", fragment);
        }
    }

    private static bool IsSafeAbsolute(string? url) =>
        url is not null && (url.StartsWith("//", StringComparison.Ordinal) ||
            url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

    private static string Render(IEnumerable<Block> blocks)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        foreach (var block in blocks) renderer.Render(block);
        renderer.Writer.Flush();
        return writer.ToString();
    }

    private static string RenderInline(Inline inline)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(inline);
        renderer.Writer.Flush();
        return writer.ToString();
    }

    private static string PlainText(HeadingBlock heading)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer) { EnableHtmlForInline = false };
        Pipeline.Setup(renderer);
        renderer.WriteLeafInline(heading);
        renderer.Writer.Flush();
        return writer.ToString().Trim();
    }

    private sealed record FrontMatter(string Html, string? Title);

    private static FrontMatter ParseFrontMatter(YamlFrontMatterBlock block)
    {
        var lines = block.Lines.Lines.Take(block.Lines.Count).Select(line => line.Slice.ToString()).ToList();
        if (lines.Count > 0 && lines[0].Trim() == "---") lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() is "---" or "...") lines.RemoveAt(lines.Count - 1);
        var text = string.Join('\n', lines);
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root)
                return new FrontMatter($"<pre class=\"front-matter\">{Encode(text)}</pre>", null);
            var html = new StringBuilder();
            var budget = 500;
            WriteYaml(html, root, 0, ref budget);
            var title = root.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: var key } &&
                key?.Equals("title", StringComparison.OrdinalIgnoreCase) == true).Value as YamlScalarNode;
            return new FrontMatter(html.ToString(), string.IsNullOrWhiteSpace(title?.Value) ? null : title.Value.Trim());
        }
        catch (YamlException)
        {
            return new FrontMatter($"<p class=\"empty\">The front matter is not valid YAML.</p><pre class=\"front-matter\">{Encode(text)}</pre>", null);
        }
    }

    private static void WriteYaml(StringBuilder html, YamlNode node, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 5)
        {
            html.Append('…');
            return;
        }
        switch (node)
        {
            case YamlScalarNode scalar:
                html.Append(Encode(scalar.Value ?? ""));
                break;
            case YamlSequenceNode sequence when sequence.Children.All(child => child is YamlScalarNode):
                html.Append("<ul class=\"chips\">");
                foreach (var child in sequence.Children)
                {
                    if (--budget < 0) break;
                    html.Append("<li>").Append(Encode(((YamlScalarNode)child).Value ?? "")).Append("</li>");
                }
                html.Append("</ul>");
                break;
            case YamlSequenceNode sequence:
                html.Append("<ol class=\"yaml-list\">");
                foreach (var child in sequence.Children)
                {
                    html.Append("<li>");
                    WriteYaml(html, child, depth + 1, ref budget);
                    html.Append("</li>");
                    if (budget < 0) break;
                }
                html.Append("</ol>");
                break;
            case YamlMappingNode mapping:
                html.Append("<dl class=\"properties\">");
                foreach (var (key, value) in mapping.Children)
                {
                    html.Append("<dt>").Append(Encode(key is YamlScalarNode { Value: { } name } ? name : "?")).Append("</dt><dd>");
                    WriteYaml(html, value, depth + 1, ref budget);
                    html.Append("</dd>");
                    if (budget < 0) break;
                }
                html.Append("</dl>");
                break;
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private sealed class Context(LocalLibraryInstance instance, NoteVault vault, HydrusInstance? hydrus, StashInstance? stash)
    {
        private readonly ILookup<string, string> _notesByName = vault.Notes.ToLookup(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        private readonly ILookup<string, string> _imagesByName = vault.Images.ToLookup(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _notes = vault.Notes.DistinctBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _images = vault.Images.DistinctBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);

        public NoteVault Vault => vault;
        public HydrusInstance? Hydrus => hydrus;
        public StashInstance? Stash => stash;
        public int EmbedCount { get; set; }

        public string ReaderUrl(string notePath, string? heading) =>
            $"/libraries/{instance.Id:D}/read/{LocalLibraryClient.NoteId(vault, notePath)}" +
            (heading is null ? "" : "#" + LinkHelper.UrilizeAsGfm(heading));

        public string FileUrl(string imagePath) =>
            $"/libraries/{instance.Id:D}/files/{string.Join('/', imagePath.Split('\\').Select(Uri.EscapeDataString))}";

        /// <summary>Foam-style resolution: path-qualified suffix match, else file name; ties go to the closest folder.</summary>
        public string? ResolveWiki(string target, string fromNote, bool images)
        {
            var normalized = target.Replace('/', '\\').Trim('\\');
            if (normalized.Length == 0) return null;
            var exact = ResolveRelative(fromNote, target, images);
            if (exact is not null) return exact;
            if (!images && NoteExtensions.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
                normalized = normalized[..^Path.GetExtension(normalized).Length];
            IEnumerable<string> candidates;
            if (normalized.Contains('\\'))
            {
                var source = images ? vault.Images : vault.Notes;
                candidates = source.Where(path =>
                {
                    var key = images ? path : path[..^Path.GetExtension(path).Length];
                    return key.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                        key.EndsWith("\\" + normalized, StringComparison.OrdinalIgnoreCase);
                });
            }
            else candidates = images ? _imagesByName[normalized] : _notesByName[normalized];
            return Closest(candidates, fromNote);
        }

        public string? ResolveRelative(string fromNote, string path, bool images)
        {
            var segments = new List<string>();
            if (!path.StartsWith('/') && !path.StartsWith('\\'))
                segments.AddRange((Path.GetDirectoryName(fromNote) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries));
            foreach (var segment in path.Split('/', '\\'))
            {
                if (segment is "" or ".") continue;
                if (segment == "..")
                {
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                }
                else segments.Add(segment);
            }
            if (segments.Count == 0) return null;
            var relative = string.Join('\\', segments);
            if (images) return _images.GetValueOrDefault(relative);
            if (_notes.TryGetValue(relative, out var note)) return note;
            return NoteExtensions.Select(extension => _notes.GetValueOrDefault(relative + extension)).FirstOrDefault(found => found is not null);
        }

        private static string? Closest(IEnumerable<string> candidates, string fromNote)
        {
            var from = (Path.GetDirectoryName(fromNote) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return candidates.OrderBy(candidate =>
            {
                var directory = (Path.GetDirectoryName(candidate) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
                var common = 0;
                while (common < from.Length && common < directory.Length &&
                    from[common].Equals(directory[common], StringComparison.OrdinalIgnoreCase)) common++;
                return from.Length - common + directory.Length - common;
            }).ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
    }

    private sealed class NoteSyntaxExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            if (!pipeline.InlineParsers.Contains<WikiLinkParser>()) pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
            if (!pipeline.InlineParsers.Contains<MediaTokenParser>()) pipeline.InlineParsers.Add(new MediaTokenParser());
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer html) return;
            html.ObjectRenderers.AddIfNotAlready<WikiLinkRenderer>();
            html.ObjectRenderers.AddIfNotAlready<MediaTokenRenderer>();
            html.ObjectRenderers.AddIfNotAlready<RawHtmlBlockRenderer>();
        }
    }

    internal sealed class WikiLinkInline : LeafInline
    {
        public required string Target { get; init; }
        public string? Heading { get; init; }
        public string? Alias { get; init; }
        public bool IsEmbed { get; init; }
        public string? Href { get; set; }
        public string? ImageSrc { get; set; }
        public string? NotePath { get; set; }
        public string Label => Alias ?? (Heading is null ? Target : Target.Length == 0 ? Heading : $"{Target} › {Heading}");
    }

    internal sealed class MediaTokenInline : LeafInline
    {
        public required string Name { get; init; }
        public long Id { get; init; }
        public long SceneId { get; init; }
        public string? Alt { get; init; }
        public required string Source { get; init; }
        public string? Href { get; set; }
        public string? ImageSrc { get; set; }
        public bool FullSize { get; set; }
    }

    private sealed class RawHtmlBlock(string html) : LeafBlock(null)
    {
        public string Html => html;
    }

    private sealed class WikiLinkParser : InlineParser
    {
        public WikiLinkParser() => OpeningCharacters = ['[', '!'];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var embed = slice.CurrentChar == '!';
            var start = slice.Start + (embed ? 1 : 0);
            var text = slice.Text;
            if (start + 1 > slice.End || text[start] != '[' || text[start + 1] != '[') return false;
            var close = text.IndexOf("]]", start + 2, Math.Min(slice.End + 1 - (start + 2), 502), StringComparison.Ordinal);
            if (close < 0) return false;
            var inner = text[(start + 2)..close];
            if (inner.Length == 0 || inner.IndexOfAny(['[', ']', '\r', '\n']) >= 0) return false;
            var (target, heading, alias) = ParseWikiTarget(inner);
            if (target.Length == 0 && heading is null) return false;
            processor.Inline = new WikiLinkInline
            {
                Target = target, Heading = heading, Alias = alias, IsEmbed = embed
            };
            slice.Start = close + 2;
            return true;
        }
    }

    private sealed class MediaTokenParser : InlineParser
    {
        private static readonly string[] Names = ["hydrusPreview", "hydrus", "stashImage", "stashVideo", "stashPerformer", "stashMarker"];

        public MediaTokenParser() => OpeningCharacters = ['@'];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var text = slice.Text;
            var position = slice.Start + 1;
            var end = slice.End;
            var name = Names.FirstOrDefault(candidate => position + candidate.Length < end &&
                string.CompareOrdinal(text, position, candidate, 0, candidate.Length) == 0 && text[position + candidate.Length] == '{');
            if (name is null) return false;
            var open = position + name.Length;
            var close = text.IndexOf('}', open + 1, Math.Min(slice.End - open, 300));
            if (close < 0) return false;
            var body = text[(open + 1)..close];
            if (body.IndexOfAny(['\r', '\n']) >= 0) return false;
            var parts = body.Split('|');
            if (name.StartsWith("hydrus", StringComparison.Ordinal) && parts.Length > 1) return false;
            var ids = parts[0].Split(':');
            if (ids.Length != (name == "stashMarker" ? 2 : 1) || !ids.All(IsId)) return false;
            processor.Inline = new MediaTokenInline
            {
                Name = name,
                Id = long.Parse(ids[^1], CultureInfo.InvariantCulture),
                SceneId = ids.Length == 2 ? long.Parse(ids[0], CultureInfo.InvariantCulture) : 0,
                Alt = parts.Skip(1).FirstOrDefault(part => !part.Contains(':') && part.Trim().Length > 0)?.Trim(),
                Source = text[slice.Start..(close + 1)]
            };
            slice.Start = close + 1;
            return true;
        }

        private static bool IsId(string value) => value.Length is > 0 and <= 18 && value.All(char.IsAsciiDigit) &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    private sealed class WikiLinkRenderer : HtmlObjectRenderer<WikiLinkInline>
    {
        protected override void Write(HtmlRenderer renderer, WikiLinkInline link)
        {
            if (!renderer.EnableHtmlForInline)
            {
                renderer.WriteEscape(link.Label);
                return;
            }
            if (link.ImageSrc is not null && link.IsEmbed)
            {
                renderer.Write("<img src=\"").WriteEscapeUrl(link.ImageSrc).Write("\" alt=\"").WriteEscape(link.Label)
                    .Write("\" loading=\"lazy\">");
                return;
            }
            if (link.Href is null)
            {
                renderer.Write("<span class=\"wikilink missing\" title=\"No matching note in this library\">")
                    .WriteEscape(link.Label).Write("</span>");
                return;
            }
            renderer.Write("<a class=\"wikilink\" href=\"").WriteEscapeUrl(link.Href).Write("\">").WriteEscape(link.Label).Write("</a>");
        }
    }

    private sealed class MediaTokenRenderer : HtmlObjectRenderer<MediaTokenInline>
    {
        protected override void Write(HtmlRenderer renderer, MediaTokenInline token)
        {
            if (!renderer.EnableHtmlForInline)
            {
                renderer.WriteEscape(token.Alt ?? token.Source);
                return;
            }
            if (token.ImageSrc is null || token.Href is null)
            {
                var backend = token.Name.StartsWith("hydrus", StringComparison.Ordinal) ? "Hydrus" : "Stash";
                renderer.Write("<span class=\"media-ref missing\" title=\"No ").Write(backend)
                    .Write(" instance is linked to this notes library\">").WriteEscape(token.Source).Write("</span>");
                return;
            }
            var alt = token.Alt ?? token.Name switch
            {
                "hydrusPreview" or "hydrus" => $"Hydrus file {token.Id}",
                "stashImage" => $"Stash image {token.Id}",
                "stashVideo" => $"Stash scene {token.Id}",
                "stashPerformer" => $"Stash performer {token.Id}",
                _ => $"Stash marker {token.Id} in scene {token.SceneId}"
            };
            renderer.Write("<span class=\"media-ref").Write(token.FullSize ? " full" : "").Write("\"><a href=\"")
                .WriteEscapeUrl(token.Href).Write("\" target=\"_blank\" rel=\"noopener noreferrer\"><img src=\"")
                .WriteEscapeUrl(token.ImageSrc).Write("\" alt=\"").WriteEscape(alt).Write("\" loading=\"lazy\"></a>");
            if (token.Alt is not null) renderer.Write("<span class=\"media-caption\">").WriteEscape(token.Alt).Write("</span>");
            renderer.Write("</span>");
        }
    }

    private sealed class RawHtmlBlockRenderer : HtmlObjectRenderer<RawHtmlBlock>
    {
        protected override void Write(HtmlRenderer renderer, RawHtmlBlock block)
        {
            renderer.EnsureLine();
            renderer.Write(block.Html);
            renderer.EnsureLine();
        }
    }

    private const string Styles = """
        :root { color-scheme: light dark; --muted: color-mix(in srgb, CanvasText 62%, Canvas); --line: color-mix(in srgb, CanvasText 18%, Canvas); --soft: color-mix(in srgb, CanvasText 6%, Canvas); --link: #176d62; }
        @media (prefers-color-scheme: dark) { :root { --link: #ffb36b; } }
        a { color: var(--link); }
        .jump { display: none; font: .85rem/1.5 system-ui, sans-serif; }
        * { box-sizing: border-box; }
        body { margin: 0; font: 1rem/1.6 Georgia, serif; overflow-wrap: anywhere; }
        .layout { display: grid; grid-template-columns: minmax(14rem, 20rem) minmax(0, 90rem); gap: 3rem; justify-content: center; padding: 1.5rem; }
        aside { position: sticky; top: 1.5rem; align-self: start; max-height: calc(100vh - 3rem); overflow: auto; font: .9rem/1.5 system-ui, sans-serif; }
        .actions { display: flex; flex-wrap: wrap; gap: .9rem; padding-bottom: .6rem; border-bottom: 1px solid var(--line); }
        .note-path { color: var(--muted); font-size: .8rem; margin: .5rem 0; }
        .panel { margin-top: .9rem; }
        .panel summary { cursor: pointer; font-size: .75rem; font-weight: 600; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); margin-bottom: .35rem; }
        .properties { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: .2rem .75rem; margin: 0; }
        .properties dt { font-weight: 600; } .properties dd { margin: 0; }
        .properties .properties { grid-column: 1 / -1; }
        .chips { list-style: none; display: flex; flex-wrap: wrap; gap: .25rem; margin: 0; padding: 0; }
        .chips li { padding: 0 .5rem; border: 1px solid var(--line); border-radius: 1rem; background: var(--soft); }
        .yaml-list { margin: 0; padding-left: 1.1rem; }
        .toc ol, .backlinks { list-style: none; margin: 0; padding: 0; }
        .toc li { padding-left: calc(var(--indent, 0) * .9rem); }
        .toc-level-1 { --indent: 1; } .toc-level-2 { --indent: 2; } .toc-level-3 { --indent: 3; }
        .empty { color: var(--muted); font-style: italic; margin: .25rem 0; }
        .front-matter { white-space: pre-wrap; }
        article > :first-child { margin-top: 0; }
        article details { margin: 1rem 0; } article details > summary { cursor: pointer; font-weight: 600; }
        pre { overflow: auto; padding: .8rem; background: var(--soft); }
        table { border-collapse: collapse; display: block; overflow: auto; } td, th { border: 1px solid var(--line); padding: .3rem .6rem; }
        img { max-width: 100%; height: auto; }
        blockquote { margin-left: 0; padding-left: 1rem; border-left: 3px solid var(--line); color: var(--muted); }
        .task-list-item { list-style: none; } .task-list-item input { margin: 0 .4rem 0 -1.3rem; }
        .wikilink.missing { color: var(--muted); text-decoration: underline dotted; cursor: help; }
        .media-ref { display: inline-flex; flex-direction: column; vertical-align: top; max-width: min(100%, 22rem); margin: .25rem; }
        .media-ref img { display: block; max-height: 22rem; border-radius: .35rem; background: var(--soft); object-fit: contain; }
        .media-ref.full { max-width: 100%; } .media-ref.full img { max-height: none; }
        .media-ref.missing { font-family: ui-monospace, monospace; font-size: .85em; color: var(--muted); }
        .media-caption { font: .8rem/1.4 system-ui, sans-serif; color: var(--muted); }
        .embed { margin: 1rem 0; padding: .25rem 0 .25rem 1rem; border-left: 3px solid var(--line); }
        .embed-title { font: .8rem/1.4 system-ui, sans-serif; margin-bottom: .4rem; }
        @media (max-width: 60rem) { .layout { grid-template-columns: minmax(0, 1fr); gap: 1rem; padding: 1rem; } aside { position: static; max-height: none; order: 1; border-top: 1px solid var(--line); padding-top: .5rem; } .jump { display: block; margin-bottom: .75rem; } }
        """;
}
