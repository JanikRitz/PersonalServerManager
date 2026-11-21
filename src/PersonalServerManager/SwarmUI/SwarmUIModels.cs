using PersonalServerManager.Feeds;
using PersonalServerManager.Search;

namespace PersonalServerManager.SwarmUI;

public sealed record SwarmUISearchPath(string Path, int Depth = SwarmUISearchPath.DefaultDepth)
{
    public const string DefaultPath = "raw";
    public const int DefaultDepth = 3;
    public const int MaximumDepth = 10;
}

public sealed record SwarmUIInstance(Guid Id, string Name, Uri BaseUrl, IReadOnlyList<SwarmUISearchPath> SearchPaths,
    string? InstallationFolder = null, int Port = SwarmUIInstance.DefaultPort, bool StartWithApp = false)
{
    public const int DefaultPort = 7801;
    public const int MaximumSearchPaths = 16;
    public static IReadOnlyList<SwarmUISearchPath> DefaultSearchPaths { get; } = [new(SwarmUISearchPath.DefaultPath)];

    public bool IsManaged => !string.IsNullOrWhiteSpace(InstallationFolder);
}

public enum SwarmUIMediaKind { Image, Video }

/// <param name="Path">Output path relative to the user's output root, e.g. "raw/2026-09-26/file.png".</param>
public sealed record SwarmUIFile(string Path, SwarmUIMediaKind Kind)
{
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool IsStarred { get; init; }
    /// <summary>Scalar generation parameters in SwarmUI order, e.g. ("seed", "504678709").</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Metadata { get; init; } = [];
}

public sealed record SwarmUIObservation(
    Guid InstanceId,
    string InstanceName,
    bool IsReachable,
    DateTimeOffset CheckedAt,
    string Message);

public interface ISwarmUIClient
{
    Task<SwarmUIObservation> ObserveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Searches every configured path with SwarmUI's server-side filter and returns at most <paramref name="limit"/> files per media kind.</summary>
    Task<IReadOnlyList<SwarmUIFile>> SearchAsync(SwarmUIInstance instance, string query, int limit, LibrarySort sort,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SwarmUIFile>> SearchFeedAsync(SwarmUIInstance instance, FeedSource source, FeedOrder order, int offset, int limit,
        CancellationToken cancellationToken = default);
}

public interface ISwarmUIInstanceStore
{
    Task<IReadOnlyList<SwarmUIInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public static class SwarmUIPaths
{
    public const int MaximumPathLength = 1024;
    private static readonly char[] ForbiddenCharacters = ['\\', ':', '*', '?', '"', '<', '>', '|', '#'];

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("The SwarmUI URL must be an HTTP(S) URL without credentials, query, or fragment.", nameof(endpoint));
        }
    }

    /// <summary>Returns true for a relative, forward-slash path without empty, "." or ".." segments.</summary>
    public static bool IsValidRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathLength ||
            path.IndexOfAny(ForbiddenCharacters) >= 0 || path.Any(char.IsControl))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Trim().Length != segment.Length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Normalizes and validates a configured search path; throws <see cref="InvalidDataException"/> when invalid.</summary>
    public static SwarmUISearchPath NormalizeSearchPath(SwarmUISearchPath searchPath)
    {
        var path = (searchPath.Path ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (!IsValidRelativePath(path))
        {
            throw new InvalidDataException("Search paths must be relative SwarmUI output folders such as 'raw'.");
        }

        if (searchPath.Depth is < 1 or > SwarmUISearchPath.MaximumDepth)
        {
            throw new InvalidDataException($"Search depth must be between 1 and {SwarmUISearchPath.MaximumDepth}.");
        }

        return new SwarmUISearchPath(path, searchPath.Depth);
    }

    public static SwarmUIMediaKind? MediaKind(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".avif" or ".bmp" => SwarmUIMediaKind.Image,
            ".mp4" or ".webm" or ".mov" or ".m4v" => SwarmUIMediaKind.Video,
            _ => null
        };

    /// <summary>True when <paramref name="filePath"/> is a valid media path below one of the instance's configured search paths.</summary>
    public static bool IsServable(SwarmUIInstance instance, string? filePath) =>
        IsValidRelativePath(filePath) && MediaKind(filePath!) is not null &&
        instance.SearchPaths.Any(searchPath => filePath!.StartsWith(searchPath.Path + "/", StringComparison.Ordinal));

    /// <summary>Builds the SwarmUI /View URL path for a validated output path, escaping each segment.</summary>
    public static string ViewPath(string filePath) =>
        "View/local/" + string.Join('/', filePath.Split('/').Select(Uri.EscapeDataString));

    public static Uri CreateFileLink(Guid instanceId, string filePath) =>
        new($"/media/swarmui/{instanceId:D}/file?path={Uri.EscapeDataString(filePath)}", UriKind.Relative);

    public static Uri CreatePreviewLink(Guid instanceId, string filePath) =>
        new($"/media/swarmui/{instanceId:D}/preview?path={Uri.EscapeDataString(filePath)}", UriKind.Relative);
}
