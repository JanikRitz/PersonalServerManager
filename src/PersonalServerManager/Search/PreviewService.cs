using System.Globalization;
using System.Net.Http.Headers;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Stash;

namespace PersonalServerManager.Search;

public sealed record PreviewImage(byte[] Content, string ContentType);

public sealed class PreviewService(HttpClient client, IStashInstanceStore stashStore, IHydrusInstanceStore hydrusStore)
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public static HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    public static Uri? CreateLink(string provider, Guid instanceId, string category, string itemId)
    {
        if (!TryPath(provider, category, itemId, out _))
        {
            return null;
        }

        return new Uri($"/previews/{provider.ToLowerInvariant()}/{instanceId:D}/{category.ToLowerInvariant()}/{itemId}", UriKind.Relative);
    }

    public async Task<PreviewImage?> FetchAsync(string provider, Guid instanceId, string category, string itemId, CancellationToken cancellationToken)
    {
        if (!TryPath(provider, category, itemId, out var path))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        Uri endpoint;
        string? apiKey;
        if (provider.Equals("stash", StringComparison.OrdinalIgnoreCase))
        {
            var instance = (await stashStore.GetAllAsync(timeout.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null) return null;
            endpoint = instance.WebUiUrl;
            apiKey = instance.ApiKey;
        }
        else
        {
            var instance = (await hydrusStore.GetAllAsync(timeout.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null) return null;
            endpoint = instance.ApiEndpoint;
            apiKey = instance.ApiKey;
        }

        HydrusClient.ValidateEndpoint(endpoint);
        var baseUri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (provider.Equals("stash", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else
                request.Headers.Add("Hydrus-Client-API-Access-Key", apiKey);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("Preview unavailable.");

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif"))
            throw new InvalidDataException("Unsupported preview content type.");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumBytes + 1 - buffer.Length)), timeout.Token);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumBytes) throw new InvalidDataException("Preview exceeded the size limit.");
        }

        timeout.Token.ThrowIfCancellationRequested();
        return new PreviewImage(buffer.ToArray(), contentType);
    }

    private static bool TryPath(string provider, string category, string itemId, out string path)
    {
        path = "";
        if (itemId.Length == 0 || itemId.Length > 19 || itemId.Any(character => character is < '0' or > '9') ||
            !long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;

        var normalizedId = id.ToString(CultureInfo.InvariantCulture);
        path = (provider.ToLowerInvariant(), category.ToLowerInvariant()) switch
        {
            ("stash", "scenes") => $"scene/{normalizedId}/screenshot",
            ("stash", "images") => $"image/{normalizedId}/thumbnail",
            ("stash", "performers") => $"performer/{normalizedId}/image",
            ("hydrus", "files") => $"get_files/thumbnail?file_id={normalizedId}",
            _ => ""
        };
        return path.Length != 0;
    }
}

public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this WebApplication app)
    {
        app.MapGet("/previews/{provider}/{instanceId:guid}/{category}/{itemId}", async (
            string provider, Guid instanceId, string category, string itemId, PreviewService previews, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                var image = await previews.FetchAsync(provider, instanceId, category, itemId, context.RequestAborted);
                return image is null ? Results.NotFound() : Results.File(image.Content, image.ContentType);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException or ArgumentException or OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization();
    }
}