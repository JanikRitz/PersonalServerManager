using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace PersonalServerManager.Hydrus;

public sealed class HydrusClientMedia(HttpClient client, IHydrusInstanceStore store)
{
    public static bool CanDisplay(string mime) => mime.Split(';', 2)[0].Trim().ToLowerInvariant() is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif" or "image/bmp" or
        "video/mp4" or "video/webm" or "video/ogg" or "application/pdf" or "text/plain";

    public static Uri CreateLink(Guid instanceId, long fileId) =>
        new($"/media/hydrus/{instanceId:D}/{fileId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative);

    public async Task ServeAsync(Guid instanceId, long fileId, HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        // Firefox applies this CSP to its standalone media document, so the file itself must be loadable.
        response.Headers.ContentSecurityPolicy = "default-src 'none'; media-src 'self'; img-src 'self'; frame-ancestors 'none'";
        response.StatusCode = StatusCodes.Status404NotFound;
        if (fileId <= 0) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var instance = (await store.GetAllAsync(timeout.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null) return;
            HydrusClient.ValidateEndpoint(instance.ApiEndpoint);
            var baseUri = new Uri(instance.ApiEndpoint.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(baseUri, $"get_files/file?file_id={fileId.ToString(CultureInfo.InvariantCulture)}"));
            if (!string.IsNullOrWhiteSpace(instance.ApiKey))
                request.Headers.Add("Hydrus-Client-API-Access-Key", instance.ApiKey);
            if (RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range) &&
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
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) ||
                (upstream.StatusCode == HttpStatusCode.PartialContent && (request.Headers.Range is null || headers.ContentRange is null)) ||
                headers.ContentType?.MediaType is not { } mediaType || !CanDisplay(mediaType))
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            response.StatusCode = (int)upstream.StatusCode;
            response.ContentType = mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ? "text/plain; charset=utf-8" : mediaType;
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
                response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }
}