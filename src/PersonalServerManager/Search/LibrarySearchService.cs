using System.Globalization;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Stash;

namespace PersonalServerManager.Search;

public enum LibraryMediaType { Image, Video, Performer, Collection, TextEbook, Comic, Studio, Tag, Marker, Note }

public record LibraryItem(string Id, string Title, string Detail, Uri? ItemUrl, Uri? PreviewUrl = null,
    LibraryMediaType MediaType = LibraryMediaType.Image, string? Description = null);

public record LibraryGroup(
    Guid InstanceId,
    string InstanceName,
    string Provider,
    string Category,
    IReadOnlyList<LibraryItem> Items,
    Uri? SearchUrl,
    string? Error);

public record LibraryTypeItem(LibraryItem Item, LibraryGroup Source);

public record LibraryTypeGroup(LibraryMediaType Type, IReadOnlyList<LibraryTypeItem> Items,
    IReadOnlyList<LibraryGroup> Sources);

public sealed class LibrarySearchService(IStashGraphQlClient stashClient, IHydrusClient hydrusClient,
    LocalLibraryClient? localClient = null)
{
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
        IReadOnlyList<LocalLibraryInstance>? localLibraries = null)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturedQuery = query.Trim();
        var tags = ParseHydrusTags(capturedQuery);
        using var semaphore = new SemaphoreSlim(6);
        var tasks = new List<Task<LibraryGroup>>();

        foreach (var instance in stashInstances)
        {
            foreach (var kind in Enum.GetValues<StashSearchKind>())
            {
                tasks.Add(SearchGroupAsync(instance.Id, instance.Name, "Stash", kind.ToString(),
                    new Uri(instance.WebUiUrl, $"{(kind == StashSearchKind.Markers ? "scenes/markers" : kind.ToString().ToLowerInvariant())}?q={Uri.EscapeDataString(capturedQuery)}"),
                    async () => (await stashClient.SearchAsync(instance, kind, capturedQuery, limit, cancellationToken))
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
                            }))
                        .ToArray()));
            }
        }

        foreach (var instance in hydrusInstances)
        {
            tasks.Add(SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Files", null,
                async () => (await hydrusClient.SearchAsync(instance, tags, limit, cancellationToken))
                    .Select(item => (Item: item, Type: HydrusMediaType(item.Mime)))
                    .Where(entry => entry.Type.HasValue)
                    .Take(limit)
                    .Select(entry => new LibraryItem(entry.Item.Id.ToString(CultureInfo.InvariantCulture), entry.Item.Title, entry.Item.Mime, null,
                        PreviewService.CreateLink("hydrus", instance.Id, "files", entry.Item.Id.ToString(CultureInfo.InvariantCulture)), entry.Type!.Value))
                    .ToArray()));
            tasks.Add(SearchGroupAsync(instance.Id, instance.Name, "Hydrus", "Tags", null,
                async () => (await hydrusClient.SearchTagsAsync(instance, capturedQuery, limit, cancellationToken))
                    .Take(limit)
                    .Select(tag => new LibraryItem(tag.Name, tag.Name, tag.Count.ToString(CultureInfo.InvariantCulture),
                        new Uri($"/?q={Uri.EscapeDataString(tag.Name)}", UriKind.Relative), MediaType: LibraryMediaType.Tag))
                    .ToArray()));
        }

        var client = localClient ?? new LocalLibraryClient();
        foreach (var instance in localLibraries ?? [])
        {
            var category = instance.Kind switch
            {
                LocalLibraryKind.Notes => "Notes",
                LocalLibraryKind.CalibreText => "Ebooks",
                LocalLibraryKind.CalibreComic => "Comics",
                _ => throw new ArgumentOutOfRangeException(nameof(localLibraries))
            };
            tasks.Add(SearchGroupAsync(instance.Id, instance.Name,
                instance.Kind == LocalLibraryKind.Notes ? "Notes" : "Calibre", category,
                new Uri($"/libraries/{instance.Id:D}?q={Uri.EscapeDataString(capturedQuery)}", UriKind.Relative),
                async () => (await client.SearchAsync(instance, capturedQuery, limit, cancellationToken)).Take(limit).ToArray()));
        }

        var groups = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return groups;

        async Task<LibraryGroup> SearchGroupAsync(
            Guid instanceId,
            string instanceName,
            string provider,
            string category,
            Uri? searchUrl,
            Func<Task<IReadOnlyList<LibraryItem>>> search)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = await search();
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryGroup(instanceId, instanceName, provider, category, items, searchUrl, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new LibraryGroup(instanceId, instanceName, provider, category, [], searchUrl,
                    "Search failed. Check the instance connection and try again.");
            }
            finally
            {
                semaphore.Release();
            }
        }
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