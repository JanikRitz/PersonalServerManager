using System.Globalization;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;

namespace PersonalServerManager.Feeds;

public sealed record FeedItem(string Id, string Title, string SourceName, string Provider, Uri MediaUrl, Uri SourceUrl, bool IsVideo);
public sealed record FeedPage(IReadOnlyList<FeedItem> Items, IReadOnlyList<string> Errors, bool HasMore);

public sealed class FeedService(
    IStashInstanceStore stashStore, IStashGraphQlClient stashClient,
    IHydrusInstanceStore hydrusStore, IHydrusClient hydrusClient,
    ISwarmUIInstanceStore swarmStore, ISwarmUIClient swarmClient)
{
    public FeedSession Start(FeedDefinition feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return new FeedSession(feed, stashStore, stashClient, hydrusStore, hydrusClient, swarmStore, swarmClient);
    }
}

public sealed class FeedSession
{
    private const int PageSize = 20;
    private const int BatchSize = 20;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<SourceState> sources;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly List<FeedItem> pendingItems = [];
    private readonly List<string> pendingErrors = [];
    private int nextSource;

    internal FeedSession(FeedDefinition feed, IStashInstanceStore stashStore, IStashGraphQlClient stashClient,
        IHydrusInstanceStore hydrusStore, IHydrusClient hydrusClient, ISwarmUIInstanceStore swarmStore, ISwarmUIClient swarmClient)
    {
        sources = feed.Sources.Select(source => new SourceState(source, feed.Order, stashStore, stashClient,
            hydrusStore, hydrusClient, swarmStore, swarmClient)).ToList();
    }

    public async Task<FeedPage> LoadNextAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while (pendingItems.Count < PageSize && sources.Any(source => !source.Exhausted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requests = new List<Task<SourceResult>>();
                for (var visited = 0; visited < sources.Count && requests.Count < PageSize - pendingItems.Count; visited++)
                {
                    var source = sources[nextSource];
                    nextSource = (nextSource + 1) % sources.Count;
                    if (!source.Exhausted) requests.Add(LoadSourceAsync(source, cancellationToken));
                }
                var results = await Task.WhenAll(requests);
                foreach (var sourceResult in results)
                {
                    pendingErrors.AddRange(sourceResult.Errors);
                    if (sourceResult.Item is not null && seen.Add(sourceResult.Item.Id)) pendingItems.Add(sourceResult.Item);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new FeedPage(pendingItems.ToArray(), pendingErrors.ToArray(), sources.Any(source => !source.Exhausted));
            pendingItems.Clear();
            pendingErrors.Clear();
            return result;
        }
        finally { gate.Release(); }
    }

    private static async Task<SourceResult> LoadSourceAsync(SourceState source, CancellationToken token)
    {
        var errors = new List<string>();
        try { return new SourceResult(await source.NextAsync(errors, token), errors); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new SourceResult(null, errors); }
        catch (MissingInstanceException)
        {
            if (!token.IsCancellationRequested)
            {
                source.Exhausted = true;
                errors.Add($"{source.Provider} source {source.InstanceId:D} instance is missing.");
            }
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                source.Exhausted = true;
                errors.Add(source.ErrorMessage(exception));
            }
        }
        return new SourceResult(null, errors);
    }

    private sealed record SourceResult(FeedItem? Item, List<string> Errors);

    private sealed class SourceState
    {
        private readonly FeedSource source;
        private readonly FeedOrder order;
        private readonly IStashInstanceStore stashStore;
        private readonly IStashGraphQlClient stashClient;
        private readonly IHydrusInstanceStore hydrusStore;
        private readonly IHydrusClient hydrusClient;
        private readonly ISwarmUIInstanceStore swarmStore;
        private readonly ISwarmUIClient swarmClient;
        private readonly List<StreamState> streams = [];
        private int nextStream;
        private bool initialized;
        private bool failed;
        private string? instanceName;
        public bool Exhausted { get => failed || (initialized && streams.All(stream => stream.Exhausted)); set => failed = value; }
        public FeedProvider Provider => source.Provider;
        public Guid InstanceId => source.InstanceId;

        public string ErrorMessage(Exception exception)
        {
            var detail = (source.Provider, exception) switch
            {
                (FeedProvider.Stash, InvalidDataException { Message: "A Stash excluded tag could not be resolved. Check excluded tag names in the feed settings and the Stash connection." or
                    "A Stash excluded performer could not be resolved. Check excluded performer names in the feed settings and the Stash connection." } known) => known.Message,
                (FeedProvider.Hydrus, InvalidDataException { Message: "Hydrus feed search exceeds the supported result size." }) =>
                    order == FeedOrder.Random ? "Random search exceeds the 100,000 matching file limit." : "Search exceeds the 100,000 file result limit.",
                (FeedProvider.Hydrus, ArgumentException argument) when MatchesArgument(argument, "Invalid Hydrus feed query.", "query") => "Invalid Hydrus feed query.",
                (FeedProvider.Hydrus, ArgumentException argument) when MatchesArgument(argument, "Hydrus feed query is too long.", "query") => "Hydrus feed query is too long.",
                (FeedProvider.Hydrus, NotSupportedException { Message: "This system predicate cannot be negated." or
                    "Multiple system predicates in an expression are not supported." or
                    "This escaped tag cannot be represented by the Hydrus API." or
                    "This system predicate is not supported in feed queries." or
                    "Unsupported Hydrus system predicate or syntax in feed query." or
                    "Conflicting system predicates cannot be combined safely." or
                    "Hydrus feed expression is too complex." } known) => known.Message,
                (FeedProvider.SwarmUI, ArgumentException argument) when MatchesArgument(argument, "Invalid SwarmUI feed query.", "source") => "Invalid SwarmUI feed query.",
                (FeedProvider.SwarmUI, ArgumentException argument) when MatchesArgument(argument, "Invalid SwarmUI feed folder.", "source") => "Invalid SwarmUI feed folder.",
                (FeedProvider.SwarmUI, ArgumentException argument) when MatchesArgument(argument, "The SwarmUI feed folder must be within a configured search path.", "source") => "The SwarmUI feed folder must be within a configured search path.",
                _ => "could not be loaded."
            };
            return $"{Provider} source {instanceName ?? InstanceId.ToString("D")} {detail}";
        }

        private static bool MatchesArgument(ArgumentException exception, string message, string parameter) =>
            exception.Message == new ArgumentException(message, parameter).Message;

        public SourceState(FeedSource source, FeedOrder order, IStashInstanceStore stashStore, IStashGraphQlClient stashClient,
            IHydrusInstanceStore hydrusStore, IHydrusClient hydrusClient, ISwarmUIInstanceStore swarmStore, ISwarmUIClient swarmClient)
        {
            this.source = source;
            this.order = order;
            this.stashStore = stashStore;
            this.stashClient = stashClient;
            this.hydrusStore = hydrusStore;
            this.hydrusClient = hydrusClient;
            this.swarmStore = swarmStore;
            this.swarmClient = swarmClient;
        }

        public async Task<FeedItem?> NextAsync(List<string> errors, CancellationToken token)
        {
            if (!initialized) await InitializeAsync(token);
            while (streams.Any(stream => !stream.Exhausted))
            {
                token.ThrowIfCancellationRequested();
                var stream = streams[nextStream];
                nextStream = (nextStream + 1) % streams.Count;
                if (stream.Exhausted) continue;
                try
                {
                    var item = await stream.NextAsync(token);
                    if (item is not null) return item;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    token.ThrowIfCancellationRequested();
                    stream.Fail();
                    errors.Add(ErrorMessage(exception));
                }
            }
            Exhausted = true;
            return null;
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            switch (source.Provider)
            {
                case FeedProvider.Stash:
                    var stash = (await stashStore.GetAllAsync(token)).FirstOrDefault(instance => instance.Id == source.InstanceId)
                        ?? throw new MissingInstanceException();
                    instanceName = stash.Name;
                    if (source.Media != FeedMedia.Images) AddStash(stash, StashSearchKind.Scenes);
                    AddStash(stash, StashSearchKind.Images);
                    break;
                case FeedProvider.Hydrus:
                    var hydrus = (await hydrusStore.GetAllAsync(token)).FirstOrDefault(instance => instance.Id == source.InstanceId)
                        ?? throw new MissingInstanceException();
                    instanceName = hydrus.Name;
                    streams.Add(new StreamState(async (offset, limit, cancellationToken) =>
                    {
                        var files = await hydrusClient.SearchFeedAsync(hydrus, source.HydrusQuery, source.Media, order, offset, limit, cancellationToken);
                        return (files.Count, files.Select(file => MapHydrus(hydrus, file)).OfType<FeedItem>().ToArray());
                    }));
                    break;
                case FeedProvider.SwarmUI:
                    var swarm = (await swarmStore.GetAllAsync(token)).FirstOrDefault(instance => instance.Id == source.InstanceId)
                        ?? throw new MissingInstanceException();
                    instanceName = swarm.Name;
                    streams.Add(new StreamState(async (offset, limit, cancellationToken) =>
                    {
                        var files = await swarmClient.SearchFeedAsync(swarm, source, order, offset, limit, cancellationToken);
                        return (files.Count, files.Select(file => MapSwarm(swarm, file)).OfType<FeedItem>().ToArray());
                    }));
                    break;
                default: throw new InvalidOperationException();
            }
            initialized = true;
        }

        private void AddStash(StashInstance instance, StashSearchKind kind) => streams.Add(new StreamState(async (offset, limit, token) =>
        {
            var requestSource = kind == StashSearchKind.Images && source.Media == FeedMedia.Videos
                ? source with { Media = FeedMedia.All } : source;
            var files = await stashClient.SearchFeedAsync(instance, kind, requestSource, order, offset, limit, token);
            return (files.Count, files.Select(file => MapStash(instance, kind, file)).OfType<FeedItem>()
                .Where(item => source.Media == FeedMedia.All || item.IsVideo == (source.Media == FeedMedia.Videos)).ToArray());
        }));

        private static FeedItem? MapStash(StashInstance instance, StashSearchKind kind, StashSearchResult file)
        {
            if (!long.TryParse(file.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !SafeSource(file.ItemUrl)) return null;
            var category = kind == StashSearchKind.Scenes ? "scenes" : "images";
            return new FeedItem($"stash:{instance.Id:D}:{category}:{file.Id}", file.Title, instance.Name, "Stash",
                new Uri($"/media/stash/{instance.Id:D}/{category}/{Uri.EscapeDataString(file.Id)}", UriKind.Relative),
                file.ItemUrl, kind == StashSearchKind.Scenes || file.IsVideo);
        }

        private static FeedItem? MapHydrus(HydrusInstance instance, HydrusFile file)
        {
            if (file.Id <= 0 || string.IsNullOrWhiteSpace(file.Mime) || !HydrusClientMedia.CanDisplay(file.Mime)) return null;
            var mime = file.Mime.Split(';', 2)[0].Trim();
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return null;
            var link = HydrusClientMedia.CreateLink(instance.Id, file.Id);
            return new FeedItem($"hydrus:{instance.Id:D}:{file.Id.ToString(CultureInfo.InvariantCulture)}", file.Title,
                instance.Name, "Hydrus", link, link, mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        }

        private static FeedItem? MapSwarm(SwarmUIInstance instance, SwarmUIFile file)
        {
            SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
            if (!SwarmUIPaths.IsServable(instance, file.Path) || SwarmUIPaths.MediaKind(file.Path) != file.Kind || !SafeSource(instance.BaseUrl)) return null;
            var baseUri = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
            return new FeedItem($"swarmui:{instance.Id:D}:{file.Path}", file.Prompt ?? Path.GetFileNameWithoutExtension(file.Path),
                instance.Name, "SwarmUI", SwarmUIPaths.CreateFileLink(instance.Id, file.Path),
                new Uri(baseUri, SwarmUIPaths.ViewPath(file.Path)), file.Kind == SwarmUIMediaKind.Video);
        }

        private static bool SafeSource(Uri uri) => !uri.IsAbsoluteUri ||
            (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo));
    }

    private sealed class StreamState(Func<int, int, CancellationToken, Task<(int Count, FeedItem[] Items)>> fetch)
    {
        private readonly Queue<FeedItem> items = new();
        private int offset;
        private bool lastBatch;
        public bool Exhausted => lastBatch && items.Count == 0;
        public void Fail() => lastBatch = true;

        public async Task<FeedItem?> NextAsync(CancellationToken token)
        {
            while (items.Count == 0 && !lastBatch)
            {
                token.ThrowIfCancellationRequested();
                var (count, batch) = await fetch(offset, BatchSize, token);
                token.ThrowIfCancellationRequested();
                offset = checked(offset + count);
                lastBatch = count < BatchSize;
                foreach (var item in batch) items.Enqueue(item);
            }
            return items.TryDequeue(out var result) ? result : null;
        }
    }

    private sealed class MissingInstanceException : Exception;
}