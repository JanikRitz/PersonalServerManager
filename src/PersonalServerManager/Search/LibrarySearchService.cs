using System.Globalization;
using System.Net;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Management;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;

namespace PersonalServerManager.Search;

public enum LibraryMediaType { Image, Video, Performer, Collection, TextEbook, Comic, Studio, Tag, Marker, Note }

public enum LibrarySort { Default, TitleAscending, TitleDescending, DateNewest, DateOldest, Random }

public record LibraryItem(string Id, string Title, string Detail, Uri? ItemUrl, Uri? PreviewUrl = null,
    LibraryMediaType MediaType = LibraryMediaType.Image, string? Description = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, string? Path = null, Uri? ReaderUrl = null)
{
    public string? FileFormat { get; init; }
    public int? ContentCount { get; init; }
    public long? FileSizeBytes { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? MarkerStart { get; init; }
    public TimeSpan? MarkerEnd { get; init; }
    public Uri? AnimatedPreviewUrl { get; init; }
    public bool AnimatedPreviewIsVideo { get; init; }
    public bool IsFavorite { get; init; }
    public decimal? RatingOutOfFive { get; init; }
}

public record LibraryGroup(
    Guid InstanceId,
    string InstanceName,
    string Provider,
    string Category,
    IReadOnlyList<LibraryItem> Items,
    Uri? SearchUrl,
    string? Error)
{
    public bool BackendUnreachable { get; init; }
}

public record LibraryTypeItem(LibraryItem Item, LibraryGroup Source);

public record LibraryTypeGroup(LibraryMediaType Type, IReadOnlyList<LibraryTypeItem> Items,
    IReadOnlyList<LibraryGroup> Sources);

public sealed class LibrarySearchService(IStashGraphQlClient stashClient, IHydrusClient hydrusClient,
    LocalLibraryClient? localClient = null, ComicCompanionService? comicSettings = null,
    ComicCompanionClient? comicClient = null, ISwarmUIClient? swarmClient = null)
{
    private const int MaximumSwarmTitleLength = 120;
    private const int MaximumSwarmDescriptionLength = 4000;

    public static IReadOnlyList<LibraryTypeItem> SortItems(IReadOnlyList<LibraryTypeItem> items, LibrarySort sort) =>
        SortItems(items, entry => entry.Item, sort);

    public static IReadOnlyList<LibraryItem> SortItems(IReadOnlyList<LibraryItem> items, LibrarySort sort) =>
        SortItems(items, item => item, sort);

    private static IReadOnlyList<T> SortItems<T>(IReadOnlyList<T> items, Func<T, LibraryItem> select, LibrarySort sort)
    {
        string Title(T entry)
        {
            var item = select(entry);
            return string.IsNullOrWhiteSpace(item.Title) ? item.Path ?? string.Empty : item.Title;
        }
        DateTimeOffset? Date(T entry) => select(entry).CreatedAt ?? select(entry).UpdatedAt;

        return sort switch
        {
            LibrarySort.TitleAscending => items.OrderBy(Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            LibrarySort.TitleDescending => items.OrderByDescending(Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            LibrarySort.DateNewest => items.OrderBy(entry => Date(entry) is null).ThenByDescending(Date).ToArray(),
            LibrarySort.DateOldest => items.OrderBy(entry => Date(entry) is null).ThenBy(Date).ToArray(),
            LibrarySort.Random => items.OrderBy(_ => Random.Shared.Next()).ToArray(),
            _ => items
        };
    }

    public static IReadOnlyList<LibraryTypeGroup> GroupByType(IReadOnlyList<LibraryGroup> groups) =>
        groups.SelectMany(source => source.Items.Select(item => new LibraryTypeItem(item, source)))
            .GroupBy(entry => entry.Item.MediaType)
            .OrderBy(group => group.Key)
            .Select(group => new LibraryTypeGroup(group.Key, InterleaveSources(group),
                group.Select(entry => entry.Source)
                    .DistinctBy(source => (source.InstanceId, source.Provider, source.Category)).ToArray()))
            .ToArray();

    private static IReadOnlyList<LibraryTypeItem> InterleaveSources(IEnumerable<LibraryTypeItem> items) =>
        items.GroupBy(entry => (entry.Source.InstanceId, entry.Source.Provider))
            .SelectMany(source => source.Select((entry, index) => (Entry: entry, Index: index)))
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Entry)
            .ToArray();

    public async Task<IReadOnlyList<LibraryGroup>> SearchAsync(
        IReadOnlyList<StashInstance> stashInstances,
        IReadOnlyList<HydrusInstance> hydrusInstances,
        string query,
        CancellationToken cancellationToken = default,
        int limit = 5,
        IReadOnlyList<LocalLibraryInstance>? localLibraries = null,
        LibrarySort sort = LibrarySort.Default,
        IReadOnlyList<SwarmUIInstance>? swarmInstances = null)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!Enum.IsDefined(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = query.Trim();
        var tags = ParseHydrusTags(capturedQuery);
        using var semaphore = new SemaphoreSlim(6);
        var tasks = new List<Task<LibraryGroup[]>>();

        foreach (var instance in stashInstances)
        {
            tasks.Add(SearchBackendAsync(backend => Enum.GetValues<StashSearchKind>().Select(kind =>
                SearchGroupAsync(instance.Id, instance.Name, "Stash", kind.ToString(),
                    instance.BrowserUrl(new Uri(instance.WebUiUrl, $"{(kind == StashSearchKind.Markers ? "scenes/markers" : kind.ToString().ToLowerInvariant())}?q={Uri.EscapeDataString(capturedQuery)}")),
                    async token => (await stashClient.SearchAsync(instance, kind, capturedQuery, limit, sort, token))
                        .Take(limit)
                        .Select(item => new LibraryItem(item.Id, item.Title, kind.ToString(), instance.BrowserUrl(item.ItemUrl),
                            PreviewService.CreateLink("stash", instance.Id, kind.ToString(), item.Id), kind switch
                            {
                                StashSearchKind.Scenes => LibraryMediaType.Video,
                                StashSearchKind.Performers => LibraryMediaType.Performer,
                                StashSearchKind.Studios => LibraryMediaType.Studio,
                                StashSearchKind.Tags => LibraryMediaType.Tag,
                                StashSearchKind.Markers => LibraryMediaType.Marker,
                                StashSearchKind.Galleries or StashSearchKind.Groups => LibraryMediaType.Collection,
                                _ => LibraryMediaType.Image
                            }, CreatedAt: item.CreatedAt, UpdatedAt: item.UpdatedAt, Path: item.Path)
                        {
                            ContentCount = item.ContentCount,
                            Duration = item.Duration,
                            MarkerStart = item.MarkerStart,
                            MarkerEnd = item.MarkerEnd,
                            AnimatedPreviewUrl = item.HasAnimatedPreview ? PreviewService.CreateLink("stash", instance.Id, kind.ToString(), item.Id, true) : null,
                            AnimatedPreviewIsVideo = item.HasAnimatedPreview && kind == StashSearchKind.Images,
                            RatingOutOfFive = item.Rating100 / 20m,
                            IsFavorite = item.IsFavorite,
                            FileFormat = kind is StashSearchKind.Images or StashSearchKind.Scenes
                                ? FormatFromPath(item.Path) : null
                        })
                        .ToArray(), backend))));
        }

        foreach (var instance in hydrusInstances)
        {
            tasks.Add(SearchBackendAsync(backend =>
            [
                SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Files", null,
                async token => (await hydrusClient.SearchExpandedAsync(instance, tags, limit, sort, token))
                    .Select(item => (Item: item, Type: HydrusMediaType(item.Mime)))
                    .Where(entry => entry.Type.HasValue)
                    .Take(limit)
                    .Select(entry => new LibraryItem(entry.Item.Id.ToString(CultureInfo.InvariantCulture), entry.Item.Title, entry.Item.Mime,
                        HydrusClientMedia.CanDisplay(entry.Item.Mime) ? HydrusClientMedia.CreateLink(instance.Id, entry.Item.Id) : null,
                        PreviewService.CreateLink("hydrus", instance.Id, "files", entry.Item.Id.ToString(CultureInfo.InvariantCulture)), entry.Type!.Value,
                        CreatedAt: entry.Item.CreatedAt, UpdatedAt: entry.Item.UpdatedAt, Path: entry.Item.Path)
                    {
                        IsFavorite = entry.Item.IsFavorite,
                        Duration = entry.Item.Duration,
                        FileFormat = entry.Type is LibraryMediaType.Image or LibraryMediaType.Video
                            ? FormatFromPath(entry.Item.Path) ?? HydrusFileFormat(entry.Item.Mime) : null
                    })
                    .ToArray(), backend),
                SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Tags", null,
                async token => (await hydrusClient.SearchTagsAsync(instance, capturedQuery, limit, sort, token))
                    .Take(limit)
                    .Select(tag => new LibraryItem(tag.Name, tag.Name, tag.Count.ToString(CultureInfo.InvariantCulture),
                        new Uri($"/?q={Uri.EscapeDataString(tag.Name)}", UriKind.Relative), MediaType: LibraryMediaType.Tag))
                        .ToArray(), backend)
                    ]));
        }

        var client = localClient ?? new LocalLibraryClient();
        foreach (var instance in localLibraries ?? [])
        {
            var category = instance.Kind switch
            {
                LocalLibraryKind.Notes => "Notes",
                LocalLibraryKind.CalibreText or LocalLibraryKind.CalibreComic or LocalLibraryKind.Calibre => "Books",
                _ => throw new ArgumentOutOfRangeException(nameof(localLibraries))
            };
            tasks.Add(Task.WhenAll(SearchGroupAsync(instance.Id, instance.Name,
                instance.Kind == LocalLibraryKind.Notes ? "Notes" : "Calibre", category,
                new Uri($"/libraries/{instance.Id:D}?q={Uri.EscapeDataString(capturedQuery)}", UriKind.Relative),
                async token => (await client.SearchAsync(instance, capturedQuery, limit, token, sort: sort)).Take(limit).ToArray())));
        }

        if (swarmClient is not null)
        {
            foreach (var instance in swarmInstances ?? [])
            {
                tasks.Add(SearchBackendAsync(backend =>
                [
                    SearchGroupAsync(instance.Id, instance.Name, "SwarmUI", "Outputs", instance.BaseUrl,
                        async token => (await swarmClient.SearchAsync(instance, capturedQuery, limit, sort, token))
                            .GroupBy(file => file.Kind)
                            .SelectMany(kind => kind.Take(limit))
                            .Select(file => SwarmItem(instance, file))
                            .ToArray(), backend)
                ]));
            }
        }

        if (comicSettings is not null && comicClient is not null)
        {
            tasks.Add(SearchComicsAsync(comicSettings, comicClient));
        }

        var groups = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return groups.SelectMany(backend => backend).ToArray();

        async Task<LibraryGroup[]> SearchComicsAsync(ComicCompanionService settingsService, ComicCompanionClient companion)
        {
            IReadOnlyList<ComicCompanionSettings> instances;
            try
            {
                instances = await settingsService.GetAllAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return [new(ComicCompanionService.LegacyInstanceId, "Comic Companion", "Comic Companion", "Comics", [], null,
                    "Comic Companion configuration could not be loaded.")];
            }
            var results = await Task.WhenAll(instances.Select(settings => SearchBackendAsync(backend =>
            [
                SearchGroupAsync(settings.Id, settings.Name, "Comic Companion", "Comics", null,
                    token => companion.SearchAsync(settings, capturedQuery, limit, sort, imagesets: false, token), backend),
                SearchGroupAsync(settings.Id, settings.Name, "Comic Companion", "Collections", null,
                    token => companion.SearchAsync(settings, capturedQuery, limit, sort, imagesets: true, token), backend)
            ])));
            return results.SelectMany(groups => groups).ToArray();
        }

        async Task<LibraryGroup[]> SearchBackendAsync(Func<CancellationTokenSource, IEnumerable<Task<LibraryGroup>>> search)
        {
            using var backend = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var results = await Task.WhenAll(search(backend));
            if (backend.IsCancellationRequested)
            {
                var reportOutage = true;
                for (var index = 0; index < results.Length; index++)
                {
                    if (results[index].Error is not null)
                    {
                        results[index] = results[index] with
                        {
                            BackendUnreachable = true,
                            Error = reportOutage ? "Backend is not reachable." : null
                        };
                        reportOutage = false;
                    }
                }
            }
            return results;
        }

        async Task<LibraryGroup> SearchGroupAsync(
            Guid instanceId,
            string instanceName,
            string provider,
            string category,
            Uri? searchUrl,
            Func<CancellationToken, Task<IReadOnlyList<LibraryItem>>> search,
            CancellationTokenSource? backend = null)
        {
            var token = backend?.Token ?? cancellationToken;
            try
            {
                await semaphore.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var items = await search(token);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new LibraryGroup(instanceId, instanceName, provider, category, items, searchUrl, null);
                }
                catch (Exception exception) when (backend is not null && !token.IsCancellationRequested && IsBackendUnreachable(exception))
                {
                    backend.Cancel();
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryGroup(instanceId, instanceName, provider, category, [], searchUrl,
                    "Search failed. Check the instance connection and try again.");
            }
        }
    }

    private static bool IsBackendUnreachable(Exception exception) => exception is
        HttpRequestException { StatusCode: null or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout }
        or TaskCanceledException;

    private static LibraryItem SwarmItem(SwarmUIInstance instance, SwarmUIFile file)
    {
        var prompt = string.IsNullOrWhiteSpace(file.Prompt) ? null
            : string.Join(' ', file.Prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var title = prompt is null ? Path.GetFileNameWithoutExtension(file.Path) : Truncate(prompt, MaximumSwarmTitleLength);
        var size = file is { Width: { } width, Height: { } height } ? $"{width}x{height}" : null;
        var detail = string.Join(" · ", new[] { file.Model, size }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new LibraryItem(file.Path, title, detail,
            SwarmUIPaths.CreateFileLink(instance.Id, file.Path), SwarmUIPaths.CreatePreviewLink(instance.Id, file.Path),
            file.Kind == SwarmUIMediaKind.Video ? LibraryMediaType.Video : LibraryMediaType.Image,
            SwarmDescription(file, size), file.CreatedAt, Path: file.Path)
        {
            IsFavorite = file.IsStarred,
            FileFormat = FormatFromPath(file.Path)
        };
    }

    private static string? SwarmDescription(SwarmUIFile file, string? size)
    {
        var metadata = file.Metadata.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToArray();
        bool Has(params string[] keys) => metadata.Any(entry => keys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase));
        string? Value(string key) => metadata.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();

        var lines = new List<string>();
        if ((file.Prompt ?? Value("prompt")) is { } prompt && !string.IsNullOrWhiteSpace(prompt))
        {
            lines.Add(prompt.Trim());
        }
        if (!Has("model") && !string.IsNullOrWhiteSpace(file.Model))
        {
            lines.Add($"Model: {file.Model}");
        }
        size ??= Value("width") is { } width && Value("height") is { } height ? $"{width}x{height}" : null;
        if (!Has("width", "height") && size is not null)
        {
            lines.Add($"Size: {size}");
        }

        var sizeWritten = false;
        foreach (var (key, value) in metadata)
        {
            switch (key.ToLowerInvariant())
            {
                case "prompt":
                    break;
                case "width" or "height":
                    if (!sizeWritten && size is not null)
                    {
                        lines.Add($"Size: {size}");
                    }
                    sizeWritten = true;
                    break;
                default:
                    lines.Add($"{SwarmMetadataLabel(key)}: {value.Trim()}");
                    break;
            }
        }

        return lines.Count == 0 ? null : Truncate(string.Join('\n', lines), MaximumSwarmDescriptionLength);
    }

    private static string SwarmMetadataLabel(string key) => key.ToLowerInvariant() switch
    {
        "negativeprompt" => "Negative prompt",
        "model" => "Model",
        "seed" => "Seed",
        "steps" => "Steps",
        "cfgscale" => "CFG scale",
        "aspectratio" => "Aspect ratio",
        "textvideoframes" => "Video frames",
        "sampler" => "Sampler",
        "scheduler" => "Scheduler",
        "generation_time" => "Generation time",
        "swarm_version" => "SwarmUI version",
        _ => key
    };

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }
        var length = char.IsHighSurrogate(value[maximumLength - 4]) ? maximumLength - 4 : maximumLength - 3;
        return value[..length].TrimEnd() + "...";
    }

    private static string? FormatFromPath(string? path) => Path.GetExtension(path) is { Length: > 1 } extension
        ? NormalizeFormat(extension[1..]) : null;

    private static string? NormalizeFormat(string format) =>
        format.Length is > 0 and <= 12 && format.All(char.IsAsciiLetterOrDigit) ? format.ToUpperInvariant() : null;

    private static string? HydrusFileFormat(string mime)
    {
        var normalized = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        var subtype = normalized[(normalized.IndexOf('/') + 1)..];
        return subtype switch
        {
            "svg+xml" => "SVG",
            "vnd.microsoft.icon" or "x-icon" => "ICO",
            "quicktime" => "MOV",
            "matroska" or "x-matroska" => "MKV",
            "x-msvideo" => "AVI",
            "x-ms-wmv" => "WMV",
            "x-ms-asf" => "ASF",
            "x-flv" => "FLV",
            _ => NormalizeFormat(subtype.StartsWith("x-", StringComparison.Ordinal) ? subtype[2..] : subtype)
        };
    }

    private static LibraryMediaType? HydrusMediaType(string mime)
    {
        var normalized = mime.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (normalized.StartsWith("image/", StringComparison.Ordinal))
        {
            return LibraryMediaType.Image;
        }

        if (normalized.StartsWith("video/", StringComparison.Ordinal))
        {
            return LibraryMediaType.Video;
        }

        if (normalized.StartsWith("text/", StringComparison.Ordinal))
        {
            return LibraryMediaType.TextEbook;
        }

        return normalized switch
        {
            "application/pdf" or "application/epub+zip" => LibraryMediaType.TextEbook,
            "application/vnd.comicbook+zip" or "application/vnd.comicbook+rar" or "application/vnd.comicbook-rar"
                or "application/x-cbz" or "application/x-cbr" or "application/x-cb7" or "application/x-cbt"
                or "application/x-cba" => LibraryMediaType.Comic,
            _ => null
        };
    }

    public static IReadOnlyList<string> ParseHydrusTags(string query) =>
        Array.AsReadOnly(query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}