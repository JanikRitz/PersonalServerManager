using PersonalServerManager.Feeds;

namespace PersonalServerManager.Hydrus;

public sealed record HydrusInstance(Guid Id, string Name, Uri ApiEndpoint, string? ApiKey,
    string? InstallationFolder = null, bool StartWithApp = false)
{
    public bool IsManaged => !string.IsNullOrWhiteSpace(InstallationFolder);
}

public sealed record HydrusFile(long Id, string Title, string Mime)
{
    public bool IsFavorite { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Path { get; init; }
}

public sealed record HydrusTag(string Name, long Count);

public sealed record HydrusObservation(
    Guid InstanceId,
    string InstanceName,
    bool IsReachable,
    DateTimeOffset CheckedAt,
    string Message);

public interface IHydrusClient
{
    Task<IReadOnlyList<HydrusFile>> SearchFeedAsync(HydrusInstance instance, string query, FeedMedia media, FeedOrder order,
        int offset, int limit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Feed search is not supported by this Hydrus client.");
    Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(HydrusInstance instance, string query, int limit,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HydrusTag>>([]);
    Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, int limit,
        CancellationToken cancellationToken = default) => SearchAsync(instance, tags, cancellationToken);
    Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, int limit,
        Search.LibrarySort sort, CancellationToken cancellationToken = default) => SearchAsync(instance, tags, limit, cancellationToken);
    Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(HydrusInstance instance, string query, int limit, Search.LibrarySort sort,
        CancellationToken cancellationToken = default) => SearchTagsAsync(instance, query, limit, cancellationToken);
    Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HydrusFile>> SearchExpandedAsync(HydrusInstance instance, IReadOnlyList<string> tags, int limit,
        Search.LibrarySort sort, CancellationToken cancellationToken = default) => SearchAsync(instance, tags, limit, sort, cancellationToken);
    Task<string> OpenExpandedSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
        OpenSearchAsync(instance, tags, cancellationToken);
}