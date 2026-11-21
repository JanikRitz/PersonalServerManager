namespace PersonalServerManager.Feeds;

public enum FeedProvider { Stash, Hydrus, SwarmUI }
public enum FeedMedia { All, Images, Videos }
public enum FeedOrder { Newest, Oldest, Random }

public sealed record FeedSource(Guid InstanceId, FeedProvider Provider)
{
    public FeedMedia Media { get; init; } = FeedMedia.All;
    public string HydrusQuery { get; init; } = "";
    public string SwarmFolder { get; init; } = "";
    public string SwarmQuery { get; init; } = "";
    public IReadOnlyList<string> IncludeTags { get; init; } = [];
    public IReadOnlyList<string> ExcludeTags { get; init; } = [];
    public IReadOnlyList<string> IncludePerformers { get; init; } = [];
    public IReadOnlyList<string> ExcludePerformers { get; init; } = [];
}

public sealed record FeedDefinition(Guid Id, string Name, FeedOrder Order, IReadOnlyList<FeedSource> Sources);

public interface IFeedStore
{
    Task<IReadOnlyList<FeedDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(FeedDefinition feed, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    Task ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);
}