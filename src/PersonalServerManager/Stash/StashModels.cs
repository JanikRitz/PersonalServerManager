namespace PersonalServerManager.Stash;

public enum StashSearchKind
{
    Scenes,
    Images,
    Performers,
    Studios,
    Tags,
    Markers,
    Galleries,
    Groups
}

public sealed record StashInstance(
    Guid Id,
    string Name,
    Uri GraphQlEndpoint,
    Uri WebUiUrl,
    string? ApiKey,
    string? InstallationFolder = null,
    int Port = 9999,
    bool StartWithApp = false)
{
    public bool IsManaged => !string.IsNullOrWhiteSpace(InstallationFolder);

    public Uri BrowserUrl(Uri url) => IsManaged
        ? new Uri($"/managed/stash/{Id:D}{url.PathAndQuery}{url.Fragment}", UriKind.Relative)
        : url;
}

public sealed record StashSearchResult(
    string Id,
    string Title,
    StashSearchKind Kind,
    Uri? ThumbnailUrl,
    Uri ItemUrl,
    Guid InstanceId,
    string InstanceName)
{
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public TimeSpan? MarkerStart { get; init; }
    public TimeSpan? MarkerEnd { get; init; }
    public bool HasAnimatedPreview { get; init; }
    public string? Path { get; init; }
    public int? ContentCount { get; init; }
    public int? Rating100 { get; init; }
    public bool IsFavorite { get; init; }
}

public sealed record StashObservation(
    Guid InstanceId,
    string InstanceName,
    bool IsReachable,
    DateTimeOffset CheckedAt,
    string Message);

public interface IStashGraphQlClient
{
    Task<IReadOnlyList<StashSearchResult>> SearchFeedAsync(
        StashInstance instance,
        StashSearchKind kind,
        Feeds.FeedSource source,
        Feeds.FeedOrder order,
        int offset,
        int limit,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<string?> GetMarkerSceneIdAsync(
        StashInstance instance,
        string markerId,
        CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        int limit,
        CancellationToken cancellationToken = default) => SearchAsync(instance, kind, query, cancellationToken);

    Task<IReadOnlyList<StashSearchResult>> SearchAsync(
        StashInstance instance,
        StashSearchKind kind,
        string query,
        int limit,
        Search.LibrarySort sort,
        CancellationToken cancellationToken = default) => SearchAsync(instance, kind, query, limit, cancellationToken);

    Task<StashObservation> ObserveAsync(
        StashInstance instance,
        CancellationToken cancellationToken = default);
}