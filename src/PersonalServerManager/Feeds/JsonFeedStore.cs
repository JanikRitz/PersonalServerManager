using System.Text.Json;
using PersonalServerManager.SwarmUI;

namespace PersonalServerManager.Feeds;

public sealed class JsonFeedStore(string filePath) : IFeedStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<FeedDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(FeedDefinition feed, CancellationToken cancellationToken = default)
    {
        feed = Validate(feed);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var feeds = await ReadAsync(cancellationToken);
            var index = feeds.FindIndex(item => item.Id == feed.Id);
            if (index < 0) feeds.Add(feed); else feeds[index] = feed;
            await WriteAsync(feeds, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var feeds = await ReadAsync(cancellationToken);
            if (feeds.RemoveAll(item => item.Id == id) > 0) await WriteAsync(feeds, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var feeds = await ReadAsync(cancellationToken);
            if (ids.Count != feeds.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => feeds.All(feed => feed.Id != id)))
                throw new InvalidDataException("Feed order must contain each feed exactly once.");
            await WriteAsync(ids.Select(id => feeds.Single(feed => feed.Id == id)).ToList(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static FeedDefinition Validate(FeedDefinition feed)
    {
        if (feed.Id == Guid.Empty || string.IsNullOrWhiteSpace(feed.Name) || feed.Name.Trim().Length > 80 ||
            !Enum.IsDefined(feed.Order) || feed.Sources is null || feed.Sources.Count > 64)
            throw new InvalidDataException("Invalid feed configuration.");
        foreach (var source in feed.Sources)
        {
            if (source is null || source.InstanceId == Guid.Empty || !Enum.IsDefined(source.Provider) || !Enum.IsDefined(source.Media) ||
                source.HydrusQuery is null || source.HydrusQuery.Length > 4096 || source.SwarmQuery is null || source.SwarmQuery.Length > 1024 ||
                source.SwarmFolder is null || (source.SwarmFolder.Length > 0 && !SwarmUIPaths.IsValidRelativePath(source.SwarmFolder)) ||
                new[] { source.IncludeTags, source.ExcludeTags, source.IncludePerformers, source.ExcludePerformers }.Any(list =>
                    list is null || list.Count > 100 || list.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024)))
                throw new InvalidDataException("Invalid feed source configuration.");
        }
        if (feed.Sources.Select(source => (source.Provider, source.InstanceId)).Distinct().Count() != feed.Sources.Count)
            throw new InvalidDataException("Each instance can only appear once in a feed.");
        return feed with { Name = feed.Name.Trim() };
    }

    private async Task<List<FeedDefinition>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(filePath);
            var feeds = await JsonSerializer.DeserializeAsync<List<FeedDefinition>>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException();
            if (feeds.Any(feed => feed is null) || feeds.Select(feed => feed.Id).Distinct().Count() != feeds.Count)
                throw new InvalidDataException();
            return feeds.Select(Validate).ToList();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException("The feed configuration file is invalid. It was left untouched.");
        }
    }

    private async Task WriteAsync(List<FeedDefinition> feeds, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, feeds, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}