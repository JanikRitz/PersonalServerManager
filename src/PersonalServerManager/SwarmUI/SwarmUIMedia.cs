using System.Net;
using System.Net.Http.Headers;

namespace PersonalServerManager.SwarmUI;

public sealed class SwarmUIMedia(HttpClient client, ISwarmUIInstanceStore store)
{
    private const int MaximumPreviewBytes = 5 * 1024 * 1024;

    public async Task ServeAsync(Guid instanceId, string? path, bool preview, HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        // Firefox applies this CSP to its standalone media document, so the file itself must be loadable.
        response.Headers.ContentSecurityPolicy = "default-src 'none'; media-src 'self'; img-src 'self'; frame-ancestors 'none'";
        response.StatusCode = StatusCodes.Status404NotFound;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var instance = (await store.GetAllAsync(timeout.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null || !SwarmUIPaths.IsServable(instance, path)) return;
            SwarmUIPaths.ValidateEndpoint(instance.BaseUrl);
            var baseUri = new Uri(instance.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(baseUri, SwarmUIPaths.ViewPath(path!) + (preview ? "?preview=true" : "")));
            if (!preview && RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range) &&
                range.Unit == "bytes" && range.Ranges.Count == 1)
                request.Headers.Range = range;

            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var headers = upstream.Content.Headers;
            if (upstream.StatusCode == HttpStatusCode.NotFound) return;
            if (upstream.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && request.Headers.Range is not null)
            {
                if (headers.ContentRange is not null) response.Headers.ContentRange = headers.ContentRange.ToString();
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }
            var mediaType = NormalizeMediaType(headers.ContentType?.MediaType);
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) ||
                (upstream.StatusCode == HttpStatusCode.PartialContent && (request.Headers.Range is null || headers.ContentRange is null)) ||
                mediaType is null || (preview && !mediaType.StartsWith("image/", StringComparison.Ordinal)) ||
                (preview && headers.ContentLength > MaximumPreviewBytes))
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            if (preview)
            {
                var bytes = await ReadPreviewAsync(upstream.Content, timeout.Token);
                if (bytes is null)
                {
                    response.StatusCode = StatusCodes.Status502BadGateway;
                    return;
                }

                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = mediaType;
                response.ContentLength = bytes.Length;
                await response.Body.WriteAsync(bytes, context.RequestAborted);
                return;
            }

            response.StatusCode = (int)upstream.StatusCode;
            response.ContentType = mediaType;
            response.ContentLength = headers.ContentLength;
            if (upstream.Headers.AcceptRanges.Contains("bytes")) response.Headers.AcceptRanges = "bytes";
            if (upstream.StatusCode == HttpStatusCode.PartialContent) response.Headers.ContentRange = headers.ContentRange!.ToString();
            await upstream.Content.CopyToAsync(response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            if (response.HasStarted) context.Abort();
            else
            {
                response.ContentType = null;
                response.ContentLength = null;
                response.Headers.Remove("Content-Range");
                response.Headers.Remove("Accept-Ranges");
                response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    // SwarmUI labels JPEG previews with the nonstandard "image/jpg".
    private static string? NormalizeMediaType(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "image/jpg" => "image/jpeg",
        ("image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif" or "image/bmp" or
            "video/mp4" or "video/webm" or "video/quicktime") and var type => type,
        _ => null
    };

    private static async Task<byte[]?> ReadPreviewAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumPreviewBytes + 1 - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumPreviewBytes) return null;
        }

        return buffer.ToArray();
    }
}
