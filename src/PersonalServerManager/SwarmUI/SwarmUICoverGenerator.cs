using System.Text.Json;

namespace PersonalServerManager.SwarmUI;

public sealed record GeneratedCover(byte[] Bytes, string ContentType);

public sealed class SwarmUIGenerationException(string message) : Exception(message);

public sealed class SwarmUICoverGenerator : IDisposable
{
    private const int MaximumImageBytes = 32 * 1024 * 1024;
    private const int MaximumJsonBytes = 48 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public SwarmUICoverGenerator(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));

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
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<GeneratedCover> GenerateAsync(SwarmUIInstance instance, string prompt, string model, int steps, double cfg,
        int width, int height, CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(instance, prompt, model, steps, cfg, width, height, null, cancellationToken);

    /// <summary>Re-renders <paramref name="image"/> at the target size as a low-creativity init image (hires-fix style upscale).</summary>
    public Task<GeneratedCover> UpscaleAsync(SwarmUIInstance instance, GeneratedCover image, string prompt, string model, int steps,
        double cfg, int width, int height, double creativity = 0.3, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Bytes is not { Length: > 0 and <= MaximumImageBytes } || ImageType(image.ContentType) is null)
            throw new ArgumentException("A valid source image is required.", nameof(image));
        if (!double.IsFinite(creativity) || creativity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(creativity));
        return GenerateCoreAsync(instance, prompt, model, steps, cfg, width, height, new Dictionary<string, object>
        {
            ["initimage"] = $"data:{ImageType(image.ContentType)};base64,{Convert.ToBase64String(image.Bytes)}",
            ["initimagecreativity"] = creativity
        }, cancellationToken);
    }

    private async Task<GeneratedCover> GenerateCoreAsync(SwarmUIInstance instance, string prompt, string model, int steps, double cfg,
        int width, int height, Dictionary<string, object>? extra, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));
        if (steps <= 0) throw new ArgumentOutOfRangeException(nameof(steps));
        if (!double.IsFinite(cfg) || cfg < 0) throw new ArgumentOutOfRangeException(nameof(cfg));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var baseUri = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var sessionResponse = await CreateSessionAsync(baseUri, timeout.Token);
                if (!sessionResponse.RootElement.TryGetProperty("session_id", out var sessionValue) ||
                    sessionValue.ValueKind != JsonValueKind.String ||
                    sessionValue.GetString() is not { Length: > 0 and <= 256 } session ||
                    session.Any(character => character is < '!' or > '~'))
                    throw new InvalidDataException("SwarmUI returned an invalid session.");

                try
                {
                    var parameters = new Dictionary<string, object>
                    {
                        ["session_id"] = session, ["images"] = 1, ["prompt"] = prompt, ["model"] = model, ["steps"] = steps,
                        ["cfgscale"] = cfg, ["width"] = width, ["height"] = height
                    };
                    foreach (var (key, value) in extra ?? []) parameters[key] = value;
                    using var response = await PostAsync(baseUri, "GenerateText2Image", parameters, MaximumJsonBytes, timeout.Token);
                    if (!response.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array ||
                        images.GetArrayLength() == 0 || images[0].ValueKind != JsonValueKind.String ||
                        images[0].GetString() is not { Length: > 0 } location)
                        throw new InvalidDataException("SwarmUI returned no image.");
                    return await ReadImageAsync(baseUri, location, timeout.Token);
                }
                catch (InvalidSessionException) when (attempt == 0)
                {
                }
                catch (InvalidSessionException)
                {
                    throw new HttpRequestException("SwarmUI rejected the session.");
                }
            }
            throw new HttpRequestException("SwarmUI rejected the session.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The SwarmUI request timed out.");
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<JsonDocument> CreateSessionAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try { return await PostAsync(baseUri, "GetNewSession", new { }, 64 * 1024, cancellationToken); }
        catch (InvalidSessionException) { throw new HttpRequestException("SwarmUI rejected the session request."); }
    }

    private async Task<JsonDocument> PostAsync(Uri baseUri, string route, object body, int maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "API/" + route))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()))
            {
                Headers = { ContentType = new("application/json") { CharSet = "utf-8" } }
            }
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        JsonDocument? document = null;
        try
        {
            var bytes = await ReadBytesAsync(response.Content, maximumBytes, cancellationToken);
            try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("SwarmUI rejected the request.", null, response.StatusCode);
            }
            catch (JsonException) { throw new InvalidDataException("SwarmUI returned invalid JSON."); }
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("SwarmUI returned invalid JSON.");
            if (document.RootElement.TryGetProperty("error_id", out var errorId) &&
                errorId.ValueKind == JsonValueKind.String && errorId.GetString() == "invalid_session_id")
                throw new InvalidSessionException();
            if (route == "GenerateText2Image" && document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String && error.GetString() is { } message && !string.IsNullOrWhiteSpace(message))
            {
                var safeMessage = new string(message.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t')
                    .Take(2000).ToArray()).Trim();
                if (safeMessage.Length > 0) throw new SwarmUIGenerationException(safeMessage);
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("SwarmUI rejected the request.", null, response.StatusCode);
            if (document.RootElement.TryGetProperty("error", out _) || document.RootElement.TryGetProperty("error_id", out _))
                throw new InvalidDataException("SwarmUI rejected the request.");
            return document;
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private async Task<GeneratedCover> ReadImageAsync(Uri baseUri, string location, CancellationToken cancellationToken)
    {
        if (location.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = location.IndexOf(',');
            if (separator < 0 || !location.AsSpan(0, separator).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SwarmUI returned an invalid image data URL.");
            var type = ImageType(location[5..(separator - 7)]);
            if (type is null || location.Length - separator - 1 > ((MaximumImageBytes + 2L) / 3 * 4))
                throw new InvalidDataException("SwarmUI returned an invalid or oversized image.");
            try
            {
                var bytes = Convert.FromBase64String(location[(separator + 1)..]);
                if (bytes.Length == 0 || bytes.Length > MaximumImageBytes) throw new InvalidDataException("SwarmUI returned an invalid or oversized image.");
                return new GeneratedCover(bytes, type);
            }
            catch (FormatException) { throw new InvalidDataException("SwarmUI returned an invalid image data URL."); }
        }

        var path = location.StartsWith('/') ? location[1..] : location;
        if (!path.StartsWith("View/local/", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#'))
            throw new InvalidDataException("SwarmUI returned an invalid image path.");
        string filePath;
        try { filePath = Uri.UnescapeDataString(path["View/local/".Length..]); }
        catch (UriFormatException) { throw new InvalidDataException("SwarmUI returned an invalid image path."); }
        if (filePath.Contains('%') || !SwarmUIPaths.IsValidRelativePath(filePath) || SwarmUIPaths.MediaKind(filePath) != SwarmUIMediaKind.Image)
            throw new InvalidDataException("SwarmUI returned an invalid image path.");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, SwarmUIPaths.ViewPath(filePath)));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("SwarmUI rejected the image request.", null, response.StatusCode);
        var contentType = ImageType(response.Content.Headers.ContentType?.MediaType);
        if (contentType is null) throw new InvalidDataException("SwarmUI returned an invalid image content type.");
        var image = await ReadBytesAsync(response.Content, MaximumImageBytes, cancellationToken);
        if (image.Length == 0) throw new InvalidDataException("SwarmUI returned an empty image.");
        return new GeneratedCover(image, contentType);
    }

    private static string? ImageType(string? type) => type?.ToLowerInvariant() switch
    {
        "image/jpg" => "image/jpeg",
        "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif" or "image/bmp" => type.ToLowerInvariant(),
        _ => null
    };

    private static async Task<byte[]> ReadBytesAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("SwarmUI response exceeded the size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1L - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes) throw new InvalidDataException("SwarmUI response exceeded the size limit.");
        }
        return buffer.ToArray();
    }

    private sealed class InvalidSessionException : Exception;
}