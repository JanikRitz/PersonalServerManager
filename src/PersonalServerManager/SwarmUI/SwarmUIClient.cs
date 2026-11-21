using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalServerManager.Feeds;
using PersonalServerManager.Search;

namespace PersonalServerManager.SwarmUI;

public sealed class SwarmUIClient : ISwarmUIClient, IDisposable
{
    private const int MaximumSessionResponseBytes = 64 * 1024;
    private const int MaximumListResponseBytes = 32 * 1024 * 1024;
    private const int MaximumValueLength = 2000;
    private const string StarredFolder = "Starred";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<Guid, string> _sessions = new();

    public SwarmUIClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? DefaultTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        handler ??= new SocketsHttpHandler();
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is HttpClientHandler clientHandler)
            {
                clientHandler.AllowAutoRedirect = false;
                clientHandler.UseCookies = false;
            }
            else if (current is SocketsHttpHandler socketsHandler)
            {
                socketsHandler.AllowAutoRedirect = false;
                socketsHandler.UseCookies = false;
            }
        }

        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SwarmUIObservation> ObserveAsync(SwarmUIInstance instance, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var (_, version) = await CreateSessionAsync(instance, timeout.Token, cancellationToken);
            var label = new string(version.Where(character => character is >= ' ' and <= '~').Take(64).ToArray()).Trim();
            return new SwarmUIObservation(instance.Id, instance.Name, true, checkedAt,
                label.Length == 0 ? "SwarmUI reachable." : $"SwarmUI {label} reachable.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or ArgumentException)
        {
            return new SwarmUIObservation(instance.Id, instance.Name, false, checkedAt, "SwarmUI is not reachable.");
        }
    }

    public async Task<IReadOnlyList<SwarmUIFile>> SearchAsync(SwarmUIInstance instance, string query, int limit, LibrarySort sort,
        CancellationToken cancellationToken = default)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!Enum.IsDefined(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024)
        {
            throw new ArgumentException("SwarmUI searches require 1 to 1024 nonblank characters.", nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
        if (instance.SearchPaths.Count == 0)
        {
            return [];
        }

        using var timeout = CreateTimeout(cancellationToken);
        await GetSessionAsync(instance, timeout.Token, cancellationToken);
        var filter = query.Trim();
        var starred = LoadStarredAsync(instance, instance.SearchPaths.Max(path => path.Depth) + 1, timeout.Token, cancellationToken);
        var listingTask = Task.WhenAll(instance.SearchPaths.Select(searchPath =>
            ListAsync(instance, searchPath.Path, searchPath.Depth, sort == LibrarySort.DateOldest, filter, timeout.Token, cancellationToken)));
        await Task.WhenAll(listingTask, starred);
        var listings = listingTask.Result;
        var starredPaths = starred.Result;

        var files = listings.SelectMany((listing, index) => listing.Select(entry =>
                (Path: $"{instance.SearchPaths[index].Path}/{entry.Source}", entry.Metadata)))
            .DistinctBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => ToFile(entry.Path, entry.Metadata, starredPaths))
            .OfType<SwarmUIFile>();
        IEnumerable<SwarmUIFile> ordered = sort switch
        {
            LibrarySort.DateOldest => files.OrderBy(file => file.CreatedAt is null).ThenBy(file => file.CreatedAt),
            LibrarySort.TitleAscending => files.OrderBy(Title, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.TitleDescending => files.OrderByDescending(Title, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.Random => files.OrderBy(_ => Random.Shared.Next()),
            _ => files.OrderBy(file => file.CreatedAt is null).ThenByDescending(file => file.CreatedAt)
        };
        return ordered.GroupBy(file => file.Kind).SelectMany(kind => kind.Take(limit)).ToArray();
    }

    public async Task<IReadOnlyList<SwarmUIFile>> SearchFeedAsync(SwarmUIInstance instance, FeedSource source, FeedOrder order,
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Provider != FeedProvider.SwarmUI || source.InstanceId != instance.Id)
            throw new ArgumentException("The feed source does not match the SwarmUI instance.", nameof(source));
        if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order));
        if (!Enum.IsDefined(source.Media)) throw new ArgumentOutOfRangeException(nameof(source));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (source.SwarmQuery is null || source.SwarmQuery.Length > 1024)
            throw new ArgumentException("Invalid SwarmUI feed query.", nameof(source));
        if (source.SwarmFolder is null)
            throw new ArgumentException("Invalid SwarmUI feed folder.", nameof(source));

        cancellationToken.ThrowIfCancellationRequested();
        SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
        var searchPaths = instance.SearchPaths.Select(SwarmUIPaths.NormalizeSearchPath).ToArray();
        if (searchPaths.Where((path, index) => path != instance.SearchPaths[index]).Any())
            throw new InvalidDataException("Invalid configured SwarmUI search path.");

        SwarmUISearchPath[] paths;
        if (string.IsNullOrWhiteSpace(source.SwarmFolder))
        {
            paths = searchPaths;
        }
        else
        {
            var folder = source.SwarmFolder;
            if (!SwarmUIPaths.IsValidRelativePath(folder))
                throw new ArgumentException("Invalid SwarmUI feed folder.", nameof(source));
            var roots = searchPaths.Where(path => folder == path.Path || folder.StartsWith(path.Path + "/", StringComparison.Ordinal)).ToArray();
            if (roots.Length == 0)
                throw new ArgumentException("The SwarmUI feed folder must be within a configured search path.", nameof(source));
            paths = [new SwarmUISearchPath(folder, roots.Max(path => path.Depth))];
        }

        if (paths.Length == 0) return [];
        using var timeout = CreateTimeout(cancellationToken);
        await GetSessionAsync(instance, timeout.Token, cancellationToken);
        var starredTask = LoadStarredAsync(instance, searchPaths.Max(path => path.Depth) + 1, timeout.Token, cancellationToken);
        var listingTask = Task.WhenAll(paths.Select(path => ListAsync(instance, path.Path, path.Depth,
            order == FeedOrder.Oldest, source.SwarmQuery.Trim(), timeout.Token, cancellationToken)));
        await Task.WhenAll(listingTask, starredTask);

        var files = listingTask.Result.SelectMany((listing, index) => listing
                .Where(entry => SwarmUIPaths.IsValidRelativePath(entry.Source))
                .Select(entry => (Path: $"{paths[index].Path}/{entry.Source}", entry.Metadata)))
            .Where(entry => SwarmUIPaths.IsServable(instance, entry.Path))
            .OrderBy(entry => entry.Metadata, StringComparer.Ordinal)
            .DistinctBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => ToFile(entry.Path, entry.Metadata, starredTask.Result))
            .OfType<SwarmUIFile>()
            .Where(file => source.Media == FeedMedia.All ||
                (source.Media == FeedMedia.Images && file.Kind == SwarmUIMediaKind.Image) ||
                (source.Media == FeedMedia.Videos && file.Kind == SwarmUIMediaKind.Video));

        IOrderedEnumerable<SwarmUIFile> ordered = order switch
        {
            FeedOrder.Oldest => files.OrderBy(file => file.CreatedAt is null).ThenBy(file => file.CreatedAt),
            FeedOrder.Random => files.OrderBy(file => Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(instance.Id.ToString("D") + "/" + file.Path))), StringComparer.Ordinal),
            _ => files.OrderBy(file => file.CreatedAt is null).ThenByDescending(file => file.CreatedAt)
        };
        return ordered.ThenBy(file => file.Path, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray();
    }

    public void Dispose() => _httpClient.Dispose();

    private static string Title(SwarmUIFile file) => file.Prompt ?? Path.GetFileName(file.Path);

    private async Task<HashSet<string>> LoadStarredAsync(SwarmUIInstance instance, int depth,
        CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        try
        {
            var listing = await ListAsync(instance, StarredFolder, depth, false, null, timeoutToken, cancellationToken);
            return listing.Select(entry => entry.Source).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
        {
            return [];
        }
    }

    private async Task<List<(string Source, string? Metadata)>> ListAsync(SwarmUIInstance instance, string path, int depth,
        bool oldestFirst, string? filter, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        using var payload = await CallAsync(instance, "ListImages",
            session => new { session_id = session, path, depth, sortBy = "Date", sortReverse = oldestFirst, filter },
            MaximumListResponseBytes, timeoutToken, cancellationToken);
        if (!payload.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("SwarmUI returned an invalid file list.");
        }

        var entries = new List<(string, string?)>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind == JsonValueKind.Object &&
                file.TryGetProperty("src", out var source) && source.ValueKind == JsonValueKind.String &&
                source.GetString() is { Length: > 0 } src)
            {
                var metadata = file.TryGetProperty("metadata", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                entries.Add((src, metadata));
            }
        }

        return entries;
    }

    private async Task<string> GetSessionAsync(SwarmUIInstance instance, CancellationToken timeoutToken, CancellationToken cancellationToken) =>
        _sessions.TryGetValue(instance.Id, out var session)
            ? session
            : (await CreateSessionAsync(instance, timeoutToken, cancellationToken)).Session;

    private async Task<(string Session, string Version)> CreateSessionAsync(SwarmUIInstance instance,
        CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        using var payload = await PostAsync(instance, "GetNewSession", new { }, MaximumSessionResponseBytes, timeoutToken, cancellationToken);
        var root = payload.RootElement;
        if (!root.TryGetProperty("session_id", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 and <= 256 } session || session.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidDataException("SwarmUI returned an invalid session.");
        }

        _sessions[instance.Id] = session;
        var version = root.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String
            ? versionValue.GetString() ?? "" : "";
        return (session, version);
    }

    private async Task<JsonDocument> CallAsync(SwarmUIInstance instance, string route, Func<string, object> createBody,
        int maximumBytes, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await GetSessionAsync(instance, timeoutToken, cancellationToken);
            try
            {
                return await PostAsync(instance, route, createBody(session), maximumBytes, timeoutToken, cancellationToken);
            }
            catch (InvalidSessionException) when (attempt == 0)
            {
                _sessions.TryRemove(KeyValuePair.Create(instance.Id, session));
            }
            catch (InvalidSessionException)
            {
                _sessions.TryRemove(KeyValuePair.Create(instance.Id, session));
                throw new HttpRequestException("SwarmUI rejected the session.");
            }
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        return timeout;
    }

    private async Task<JsonDocument> PostAsync(SwarmUIInstance instance, string route, object body, int maximumBytes,
        CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
        try
        {
            var baseUri = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
            // SwarmUI rejects chunked bodies, so send a buffered payload with Content-Length.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "API/" + route))
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()))
                {
                    Headers = { ContentType = new("application/json") { CharSet = "utf-8" } }
                }
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
            JsonDocument? payload = null;
            try
            {
                payload = await ReadJsonAsync(response, maximumBytes, timeoutToken);
            }
            catch (InvalidDataException) when (!response.IsSuccessStatusCode)
            {
            }

            try
            {
                if (payload?.RootElement.TryGetProperty("error_id", out var errorId) == true &&
                    errorId.ValueKind == JsonValueKind.String && errorId.GetString() == "invalid_session_id")
                {
                    throw new InvalidSessionException();
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException("SwarmUI rejected the request.", null, response.StatusCode);
                }

                if (payload!.RootElement.TryGetProperty("error", out _) || payload.RootElement.TryGetProperty("error_id", out _))
                {
                    throw new InvalidDataException("SwarmUI rejected the request.");
                }

                return payload;
            }
            catch
            {
                payload?.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new HttpRequestException("The SwarmUI request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("The SwarmUI request failed.", null, exception.StatusCode);
        }
        catch (IOException)
        {
            throw new InvalidDataException("SwarmUI returned an invalid or oversized response.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maximumBytes, CancellationToken timeoutToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("SwarmUI response exceeded the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1 - buffer.Length)), timeoutToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes)
            {
                throw new InvalidDataException("SwarmUI response exceeded the size limit.");
            }
        }

        timeoutToken.ThrowIfCancellationRequested();
        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("SwarmUI returned an invalid response.");
        }

        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            payload.Dispose();
            throw new InvalidDataException("SwarmUI returned an invalid response.");
        }

        return payload;
    }

    private static SwarmUIFile? ToFile(string path, string? metadata, HashSet<string> starred)
    {
        if (!SwarmUIPaths.IsValidRelativePath(path) || SwarmUIPaths.MediaKind(path) is not { } kind)
        {
            return null;
        }

        JsonDocument? document = null;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            try
            {
                document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
            }
        }

        using (document)
        {
            var root = document?.RootElement;
            var parameters = Property(root, "sui_image_params");
            var extra = Property(root, "sui_extra_data");
            var values = new List<KeyValuePair<string, string>>();
            if (parameters is { } parameterObject)
            {
                foreach (var property in parameterObject.EnumerateObject())
                {
                    if (Scalar(property.Value) is { } value)
                    {
                        values.Add(new(property.Name, value));
                    }
                }
            }

            if (Scalar(Property(extra, "generation_time")) is { } generationTime)
            {
                values.Add(new("generation_time", generationTime));
            }

            return new SwarmUIFile(path, kind)
            {
                Prompt = Text(Property(parameters, "prompt")),
                Model = Text(Property(parameters, "model")),
                Width = Dimension(Property(parameters, "width")),
                Height = Dimension(Property(parameters, "height")),
                CreatedAt = CreatedAt(path, Text(Property(extra, "date"))),
                IsStarred = starred.Contains(path),
                Metadata = values
            };
        }
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static string? Text(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())
            ? Truncate(value.GetString()!) : null;

    private static string? Scalar(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.String => Text(element),
        JsonValueKind.Number => Truncate(element.Value.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static string Truncate(string value) => value.Length <= MaximumValueLength ? value : value[..MaximumValueLength];

    private static int? Dimension(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) && number > 0 ? number : null;

    private static DateTimeOffset? CreatedAt(string path, string? metadataDate)
    {
        var segments = path.Split('/');
        var found = TryParseDate(metadataDate, out var date);
        for (var index = segments.Length - 2; !found && index >= 0; index--)
        {
            found = TryParseDate(segments[index], out date);
        }

        if (!found)
        {
            return null;
        }

        var name = segments[^1];
        var digits = name.TakeWhile(char.IsAsciiDigit).Count();
        if (digits >= 4 && digits < name.Length && name[digits] == '-' &&
            int.Parse(name.AsSpan(0, 2), CultureInfo.InvariantCulture) is var hour and < 24 &&
            int.Parse(name.AsSpan(2, 2), CultureInfo.InvariantCulture) is var minute and < 60)
        {
            date = date.AddHours(hour).AddMinutes(minute);
        }

        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    private static bool TryParseDate(string? value, out DateTime date) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private sealed class InvalidSessionException : Exception;
}
