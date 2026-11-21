using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PersonalServerManager.Search;

namespace PersonalServerManager.Hydrus;

public sealed class HydrusClient : IHydrusClient, IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly string DefaultFavouritesServiceKey = Convert.ToHexStringLower("favourites"u8);
    // Hydrus has no tag-search limit and returns every match, so short queries can be several megabytes.
    private const int MaximumTagResponseBytes = 16 * 1024 * 1024;
    private const string ResponseTooLarge = "Hydrus response exceeded the size limit.";
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    public HydrusClient(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? MaximumTimeout;
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

    public async Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var payload = await SendAsync(instance, HttpMethod.Get, "verify_access_key", null, timeout.Token, cancellationToken);
            var root = payload.RootElement;
            if (root.GetProperty("permits_everything").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                root.GetProperty("basic_permissions").ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Hydrus returned an invalid response.");
            }

            return new HydrusObservation(instance.Id, instance.Name, true, checkedAt, "Hydrus access key verified.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new HydrusObservation(instance.Id, instance.Name, false, checkedAt, "Hydrus access verification failed.");
        }
    }

    public Task<IReadOnlyList<HydrusFile>> SearchAsync(
        HydrusInstance instance,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default) => SearchAsync(instance, tags, 5, cancellationToken);

    public Task<IReadOnlyList<HydrusFile>> SearchAsync(
        HydrusInstance instance,
        IReadOnlyList<string> tags,
        int limit,
        CancellationToken cancellationToken = default) => SearchAsync(instance, tags, limit, LibrarySort.Default, cancellationToken);

    public Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags,
        int limit, LibrarySort sort, CancellationToken cancellationToken = default) =>
        SearchFilesAsync(instance, tags, limit, sort, false, cancellationToken);

    public Task<IReadOnlyList<HydrusFile>> SearchExpandedAsync(HydrusInstance instance, IReadOnlyList<string> tags,
        int limit, LibrarySort sort, CancellationToken cancellationToken = default) =>
        SearchFilesAsync(instance, tags, limit, sort, true, cancellationToken);

    private async Task<IReadOnlyList<HydrusFile>> SearchFilesAsync(
        HydrusInstance instance,
        IReadOnlyList<string> tags,
        int limit,
        LibrarySort sort,
        bool expandTags,
        CancellationToken cancellationToken)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        // Hydrus file titles are synthetic, so title sorts keep the default order and are sorted locally.
        var order = sort switch
        {
            LibrarySort.DateNewest => "&file_sort_type=2&file_sort_asc=false",
            LibrarySort.DateOldest => "&file_sort_type=2&file_sort_asc=true",
            LibrarySort.Random => "&file_sort_type=4",
            LibrarySort.Default or LibrarySort.TitleAscending or LibrarySort.TitleDescending => "",
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        ValidateTags(tags);
        IEnumerable<object> predicates = expandTags ? await ResolveSearchPredicatesAsync(instance, tags, cancellationToken) : tags;
        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var search = await SendAsync(instance, HttpMethod.Get,
                $"get_files/search_files?tags={Uri.EscapeDataString(JsonSerializer.Serialize(predicates.Append($"system:limit={limit}")))}&return_file_ids=true&return_hashes=false{order}",
                null, timeout.Token, cancellationToken);
            var ids = ReadIds(search.RootElement, limit);
            if (ids.Length == 0)
            {
                return [];
            }

            var metadataPath = $"get_files/file_metadata?file_ids={Uri.EscapeDataString(JsonSerializer.Serialize(ids))}";
            try
            {
                using var metadata = await SendAsync(instance, HttpMethod.Get,
                    $"{metadataPath}&only_return_basic_information=false&include_services_object=false",
                    null, timeout.Token, cancellationToken);
                return ReadFiles(metadata.RootElement, ids, limit);
            }
            catch (InvalidDataException exception) when (exception.Message == ResponseTooLarge)
            {
                using var metadata = await SendAsync(instance, HttpMethod.Get,
                    $"{metadataPath}&only_return_basic_information=true&include_services_object=false",
                    null, timeout.Token, cancellationToken);
                return ReadFiles(metadata.RootElement, ids, limit);
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(
        HydrusInstance instance,
        string query,
        int limit,
        CancellationToken cancellationToken = default) => SearchTagsAsync(instance, query, limit, LibrarySort.Default, cancellationToken);

    public async Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(
        HydrusInstance instance,
        string query,
        int limit,
        LibrarySort sort,
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
            throw new ArgumentException("Hydrus tag searches require 1 to 1024 nonblank characters.", nameof(query));
        }

        var tags = await FindTagsAsync(instance, query, cancellationToken);
        IEnumerable<HydrusTag> ordered = sort switch
        {
            LibrarySort.TitleAscending => tags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.TitleDescending => tags.OrderByDescending(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.Random => tags.OrderBy(_ => Random.Shared.Next()),
            _ => tags
        };
        return ordered.Take(limit).ToArray();
    }

    private async Task<List<HydrusTag>> FindTagsAsync(HydrusInstance instance, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CreateTimeout(cancellationToken);
        using var payload = await SendAsync(instance, HttpMethod.Get,
            $"add_tags/search_tags?search={Uri.EscapeDataString(query)}&tag_display_type=display",
            null, timeout.Token, cancellationToken, MaximumTagResponseBytes);
        if (!payload.RootElement.TryGetProperty("tags", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hydrus returned an invalid tag list.");
        }

        var tags = new List<HydrusTag>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("value", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                nameValue.GetString() is not { Length: > 0 and <= 1024 } name || string.IsNullOrWhiteSpace(name) ||
                !value.TryGetProperty("count", out var countValue) || countValue.ValueKind != JsonValueKind.Number ||
                !countValue.TryGetInt64(out var count) || count < 0)
            {
                throw new InvalidDataException("Hydrus returned invalid tag data.");
            }

            tags.Add(new HydrusTag(name, count));
        }

        return tags;
    }

    public Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        ValidateTags(tags);
        return OpenPageAsync(instance, tags, cancellationToken);
    }

    public async Task<string> OpenExpandedSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        ValidateTags(tags);
        return await OpenPageAsync(instance, await ResolveSearchPredicatesAsync(instance, tags, cancellationToken), cancellationToken);
    }

    private async Task<string> OpenPageAsync(
        HydrusInstance instance,
        IReadOnlyList<object> tags,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var payload = await SendAsync(instance, HttpMethod.Post, "manage_pages/new_page",
            new { page_type = 6, page_name = "Personal Server Manager", tags, focus_page = true }, timeout.Token, cancellationToken);
        if (!payload.RootElement.TryGetProperty("page_key", out var key) || key.ValueKind != JsonValueKind.String ||
            key.GetString() is not { Length: > 0 and <= 128 } pageKey || !pageKey.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Hydrus returned an invalid page key.");
        }

        return pageKey;
    }

    public void Dispose() => _httpClient.Dispose();

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        return timeout;
    }

    private async Task<JsonDocument> SendAsync(
        HydrusInstance instance, HttpMethod method, string path, object? body,
        CancellationToken timeoutToken, CancellationToken cancellationToken, int maximumBytes = MaximumResponseBytes)
    {
        ValidateEndpoint(instance.ApiEndpoint);
        if (instance.ApiKey is { } apiKey && (apiKey.Length > 1024 || apiKey.Any(character => character < ' ' || character > '~')))
        {
            throw new ArgumentException("The Hydrus API key contains invalid characters.", nameof(instance));
        }

        try
        {
            var baseUri = new Uri(instance.ApiEndpoint.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                request.Headers.Add("Hydrus-Client-API-Access-Key", instance.ApiKey);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Hydrus rejected the request.", null, response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new InvalidDataException(ResponseTooLarge);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
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
                    throw new InvalidDataException(ResponseTooLarge);
                }
            }

            timeoutToken.ThrowIfCancellationRequested();
            var payload = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 32 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                payload.Dispose();
                throw new InvalidDataException("Hydrus returned an invalid response.");
            }

            return payload;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new HttpRequestException("The Hydrus request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("The Hydrus request failed.", null, exception.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new InvalidDataException("Hydrus returned an invalid or oversized response.");
        }
    }

    internal static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("The Hydrus endpoint must be an HTTP(S) URL without credentials, query, or fragment.", nameof(endpoint));
        }
    }

    private async Task<IReadOnlyList<object>> ResolveSearchPredicatesAsync(HydrusInstance instance,
        IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var predicates = tags.Cast<object>().ToArray();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        var lookups = 0;
        for (var index = 0; index < tags.Count && lookups < 8 && !budget.IsCancellationRequested; index++)
        {
            var tag = tags[index];
            if (tag.Contains(':') || tag.Contains('*') || tag.StartsWith('-')) continue;
            lookups++;
            try
            {
                var matches = await FindTagsAsync(instance, tag, budget.Token);
                var alternatives = matches.Select(match => match.Name)
                    .Where(name => name.IndexOf(':') is > 0 and var separator && !name.Contains('*') &&
                        name.AsSpan(separator + 1).Equals(tag, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(16).Prepend(tag).ToArray();
                if (alternatives.Length > 1) predicates[index] = alternatives;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return predicates;
    }

    private static void ValidateTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > 64 || tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 1024))
        {
            throw new ArgumentException("Hydrus searches support at most 64 nonempty predicates of at most 1024 characters.", nameof(tags));
        }
    }

    private static long[] ReadIds(JsonElement root, int limit)
    {
        if (!root.TryGetProperty("file_ids", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hydrus returned an invalid file ID list.");
        }

        var ids = new List<long>();
        foreach (var value in values.EnumerateArray().Take(limit))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0)
            {
                throw new InvalidDataException("Hydrus returned an invalid file ID.");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    private static IReadOnlyList<HydrusFile> ReadFiles(JsonElement root, long[] ids, int limit)
    {
        if (!root.TryGetProperty("metadata", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > limit)
        {
            throw new InvalidDataException("Hydrus returned invalid file metadata.");
        }

        var files = new Dictionary<long, HydrusFile>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("file_id", out var identifier) || identifier.ValueKind != JsonValueKind.Number ||
                !identifier.TryGetInt64(out var id) || !ids.Contains(id) || files.ContainsKey(id) ||
                !value.TryGetProperty("mime", out var mimeValue) || mimeValue.ValueKind != JsonValueKind.String ||
                mimeValue.GetString() is not { Length: > 0 and <= 128 } mime)
            {
                throw new InvalidDataException("Hydrus returned invalid file metadata.");
            }

            DateTimeOffset? createdAt = null;
            if (value.TryGetProperty("file_services", out var services) && services.ValueKind == JsonValueKind.Object &&
                services.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                foreach (var service in current.EnumerateObject())
                {
                    var imported = ReadTimestamp(service.Value, "time_imported");
                    if (imported.HasValue && (!createdAt.HasValue || imported < createdAt)) createdAt = imported;
                }
            }
            files.Add(id, new HydrusFile(id, $"File {id.ToString(CultureInfo.InvariantCulture)}", mime)
            {
                IsFavorite = value.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Object &&
                    ratings.TryGetProperty(DefaultFavouritesServiceKey, out var favorite) && favorite.ValueKind == JsonValueKind.True,
                CreatedAt = createdAt,
                UpdatedAt = ReadTimestamp(value, "time_modified"),
                Duration = ReadDuration(value, "duration")
            });
        }

        return ids.Where(files.ContainsKey).Select(id => files[id]).ToArray();
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static TimeSpan? ReadDuration(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds) || milliseconds <= 0 ||
            milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            return null;

        return TimeSpan.FromMilliseconds(milliseconds);
    }
}