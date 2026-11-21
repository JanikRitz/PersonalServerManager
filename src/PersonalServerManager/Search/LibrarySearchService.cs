using System.Globalization;
using System.Net;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Stash;

namespace PersonalServerManager.Search;

public enum LibraryMediaType { Image, Video, Performer, Collection, TextEbook, Comic, Studio, Tag, Marker, Note }

public enum LibrarySort { Default, TitleAscending, TitleDescending, DateNewest, DateOldest, Random }

public record LibraryItem(string Id, string Title, string Detail, Uri? ItemUrl, Uri? PreviewUrl = null,
    LibraryMediaType MediaType = LibraryMediaType.Image, string? Description = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, string? Path = null, Uri? ReaderUrl = null);

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
    LocalLibraryClient? localClient = null)
{
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
        LibrarySort sort = LibrarySort.Default)
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
                    new Uri(instance.WebUiUrl, $"{(kind == StashSearchKind.Markers ? "scenes/markers" : kind.ToString().ToLowerInvariant())}?q={Uri.EscapeDataString(capturedQuery)}"),
                    async token => (await stashClient.SearchAsync(instance, kind, capturedQuery, limit, sort, token))
                        .Take(limit)
                        .Select(item => new LibraryItem(item.Id, item.Title, kind.ToString(), item.ItemUrl,
                            PreviewService.CreateLink("stash", instance.Id, kind.ToString(), item.Id), kind switch
                            {
                                StashSearchKind.Scenes => LibraryMediaType.Video,
                                StashSearchKind.Performers => LibraryMediaType.Performer,
                                StashSearchKind.Studios => LibraryMediaType.Studio,
                                StashSearchKind.Tags => LibraryMediaType.Tag,
                                StashSearchKind.Markers => LibraryMediaType.Marker,
                                StashSearchKind.Galleries or StashSearchKind.Groups => LibraryMediaType.Collection,
                                _ => LibraryMediaType.Image
                            }, CreatedAt: item.CreatedAt, UpdatedAt: item.UpdatedAt, Path: item.Path))
                        .ToArray(), backend))));
        }

        foreach (var instance in hydrusInstances)
        {
            tasks.Add(SearchBackendAsync(backend =>
            [
                SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Files", null,
                async token => (await hydrusClient.SearchAsync(instance, tags, limit, sort, token))
                    .Select(item => (Item: item, Type: HydrusMediaType(item.Mime)))
                    .Where(entry => entry.Type.HasValue)
                    .Take(limit)
                    .Select(entry => new LibraryItem(entry.Item.Id.ToString(CultureInfo.InvariantCulture), entry.Item.Title, entry.Item.Mime,
                        HydrusClientMedia.CanDisplay(entry.Item.Mime) ? HydrusClientMedia.CreateLink(instance.Id, entry.Item.Id) : null,
                        PreviewService.CreateLink("hydrus", instance.Id, "files", entry.Item.Id.ToString(CultureInfo.InvariantCulture)), entry.Type!.Value,
                        CreatedAt: entry.Item.CreatedAt, UpdatedAt: entry.Item.UpdatedAt, Path: entry.Item.Path))
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

        var groups = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return groups.SelectMany(backend => backend).ToArray();

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