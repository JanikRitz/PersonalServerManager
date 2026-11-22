using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using PersonalServerManager.Hydrus;

namespace PersonalServerManager.Stash;

public sealed class StashMedia(HttpClient client, IStashInstanceStore store)
{
    public static HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    public Task ServeAsync(Guid instanceId, string kind, long id, HttpContext context) =>
        ServeAsync(instanceId, kind, id, null, context);

    public async Task ServeAsync(Guid instanceId, string kind, long id, long? sceneId, HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; media-src 'self'; img-src 'self'; frame-ancestors 'none'";
        response.StatusCode = StatusCodes.Status404NotFound;

        var path = (kind, id > 0, sceneId is > 0) switch
        {
            ("images", true, false) when sceneId is null => $"image/{id.ToString(CultureInfo.InvariantCulture)}/image",
            ("image-previews", true, false) when sceneId is null => $"image/{id.ToString(CultureInfo.InvariantCulture)}/preview",
            ("scenes", true, false) when sceneId is null => $"scene/{id.ToString(CultureInfo.InvariantCulture)}/stream",
            ("scene-previews", true, false) when sceneId is null => $"scene/{id.ToString(CultureInfo.InvariantCulture)}/preview",
            ("scene-webp", true, false) when sceneId is null => $"scene/{id.ToString(CultureInfo.InvariantCulture)}/webp",
            ("markers", true, true) => $"scene/{sceneId!.Value.ToString(CultureInfo.InvariantCulture)}/scene_marker/{id.ToString(CultureInfo.InvariantCulture)}/stream",
            ("marker-previews", true, true) => $"scene/{sceneId!.Value.ToString(CultureInfo.InvariantCulture)}/scene_marker/{id.ToString(CultureInfo.InvariantCulture)}/preview",
            _ => null
        };
        if (path is null) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var instance = (await store.GetAllAsync(timeout.Token)).FirstOrDefault(item => item.Id == instanceId);
            if (instance is null) return;
            HydrusClient.ValidateEndpoint(instance.WebUiUrl);
            var baseUri = new Uri(instance.WebUiUrl.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            if (!string.IsNullOrWhiteSpace(instance.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.ApiKey);
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

            var mediaType = headers.ContentType?.MediaType?.ToLowerInvariant();
            var isVideoType = mediaType is "video/mp4" or "video/webm" or "video/ogg" or "video/quicktime";
            var isImageType = mediaType is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif" or "image/bmp";
            var allowed = kind switch
            {
                "scenes" or "scene-previews" or "markers" => isVideoType,
                "scene-webp" => mediaType is "image/webp" or "image/gif",
                "image-previews" or "marker-previews" => isVideoType || mediaType is "image/webp" or "image/gif" or "image/jpeg" or "image/png",
                "images" => isVideoType || isImageType,
                _ => false
            };
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) ||
                (upstream.StatusCode == HttpStatusCode.PartialContent && (request.Headers.Range is null || headers.ContentRange is null)) || !allowed)
            {
                response.StatusCode = StatusCodes.Status502BadGateway;
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
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ArgumentException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
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
}

public static class StashMediaEndpoints
{
    public static void MapStashMediaEndpoints(this WebApplication app)
    {
        app.MapGet("/media/stash/{instanceId:guid}/{kind}/{id:long}",
            (Guid instanceId, string kind, long id, StashMedia media, HttpContext context) => media.ServeAsync(instanceId, kind, id, context))
            .RequireAuthorization();
        app.MapGet("/media/stash/{instanceId:guid}/{kind}/{sceneId:long}/{id:long}",
            (Guid instanceId, string kind, long sceneId, long id, StashMedia media, HttpContext context) => media.ServeAsync(instanceId, kind, id, sceneId, context))
            .RequireAuthorization();
    }
}