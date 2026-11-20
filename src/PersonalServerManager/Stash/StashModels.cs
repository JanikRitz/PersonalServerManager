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
    string? ApiKey);

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
    public string? Path { get; init; }
}

public sealed record StashObservation(
    Guid InstanceId,
    string InstanceName,
    bool IsReachable,
    DateTimeOffset CheckedAt,
    string Message);

public interface IStashGraphQlClient
{
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

    Task<StashObservation> ObserveAsync(
        StashInstance instance,
        CancellationToken cancellationToken = default);
}