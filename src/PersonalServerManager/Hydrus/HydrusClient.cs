using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalServerManager.Hydrus;

public sealed class HydrusClient : IHydrusClient, IDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
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

    public async Task<IReadOnlyList<HydrusFile>> SearchAsync(
        HydrusInstance instance,
        IReadOnlyList<string> tags,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ValidateTags(tags);
        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            using var search = await SendAsync(instance, HttpMethod.Get,
                $"get_files/search_files?tags={Uri.EscapeDataString(JsonSerializer.Serialize(tags.Append($"system:limit={limit}")))}&return_file_ids=true&return_hashes=false",
                null, timeout.Token, cancellationToken);
            var ids = ReadIds(search.RootElement, limit);
            if (ids.Length == 0)
            {
                return [];
            }

            using var metadata = await SendAsync(instance, HttpMethod.Get,
                $"get_files/file_metadata?file_ids={Uri.EscapeDataString(JsonSerializer.Serialize(ids))}&only_return_basic_information=true&include_services_object=false",
                null, timeout.Token, cancellationToken);
            return ReadFiles(metadata.RootElement, ids, limit);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public async Task<IReadOnlyList<HydrusTag>> SearchTagsAsync(
        HydrusInstance instance,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is not (5 or 25))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024)
        {
            throw new ArgumentException("Hydrus tag searches require 1 to 1024 nonblank characters.", nameof(query));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CreateTimeout(cancellationToken);
        using var payload = await SendAsync(instance, HttpMethod.Get,
            $"add_tags/search_tags?search={Uri.EscapeDataString(query)}&tag_display_type=display",
            null, timeout.Token, cancellationToken);
        if (!payload.RootElement.TryGetProperty("tags", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hydrus returned an invalid tag list.");
        }

        var tags = new List<HydrusTag>();
        foreach (var value in values.EnumerateArray().Take(limit))
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

    public async Task<string> OpenSearchAsync(
        HydrusInstance instance,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        ValidateTags(tags);
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
        CancellationToken timeoutToken, CancellationToken cancellationToken)
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

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidDataException("Hydrus response exceeded the size limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumResponseBytes + 1 - buffer.Length)), timeoutToken);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumResponseBytes)
                {
                    throw new InvalidDataException("Hydrus response exceeded the size limit.");
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

            files.Add(id, new HydrusFile(id, $"File {id.ToString(CultureInfo.InvariantCulture)}", mime));
        }

        return ids.Where(files.ContainsKey).Select(id => files[id]).ToArray();
    }
}