using System.Net;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.RandomPlot;
using PersonalServerManager.Stash;
using Yarp.ReverseProxy.Forwarder;

namespace PersonalServerManager.Management;

public static class ManagedServiceProxy
{
    private static readonly HttpMessageInvoker Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    });

    public static void MapManagedServiceProxy(this WebApplication app)
    {
        app.Map("/managed/{provider}/{instanceId:guid}/{**path}", async (HttpContext context,
            string provider, Guid instanceId, IStashInstanceStore stashStore, ILocalLibraryStore libraries,
            ManagedCalibreServer calibre, ComicCompanionService comicCompanion, IHttpForwarder forwarder, IServiceProvider services) =>
        {
            Uri? upstream = null;
            string? authorization = null;
            var prefix = $"/managed/{provider}/{instanceId:D}";
            if (provider == "stash")
            {
                var instance = (await stashStore.GetAllAsync(context.RequestAborted))
                    .FirstOrDefault(item => item.Id == instanceId && item.IsManaged);
                if (instance is not null && instance.Port is >= 1024 and <= 65535)
                    upstream = new Uri($"http://127.0.0.1:{instance.Port}/");
            }
            else if (provider == "calibre")
            {
                var instance = (await libraries.GetAllAsync(context.RequestAborted))
                    .FirstOrDefault(item => item.Id == instanceId && item.IsManaged);
                if (instance is not null)
                {
                    upstream = calibre.WithReaderConnection(instance).ContentServerUrl;
                    authorization = calibre.GetReaderAuthorization(instance);
                    if (authorization is null) upstream = null;
                }
            }
            else if (provider == "comiccompanion")
            {
                var instance = (await comicCompanion.GetAllAsync(context.RequestAborted))
                    .FirstOrDefault(item => item.Id == instanceId && item.IsManaged);
                upstream = instance?.ApiUrl;
            }
            else if (provider == "randomplot")
            {
                var store = services.GetService<IRandomPlotInstanceStore>();
                var instance = store is null ? null : (await store.GetAllAsync(context.RequestAborted))
                    .FirstOrDefault(item => item.Id == instanceId && item.IsManaged);
                if (instance is not null && instance.Port is >= 1 and <= 65535)
                    upstream = new Uri($"http://127.0.0.1:{instance.Port}/");
            }

            if (upstream is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await forwarder.SendAsync(context, upstream.GetLeftPart(UriPartial.Authority), Client,
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(10) },
                new ManagedTransformer(prefix, provider == "stash", provider == "randomplot", authorization));
        }).RequireAuthorization();
    }

    private sealed class ManagedTransformer(string prefix, bool stripPrefix, bool useUpstreamHost, string? authorization) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);
            var path = context.Request.Path;
            if (stripPrefix && path.StartsWithSegments(prefix, out var remaining))
                path = remaining.HasValue ? remaining : "/";
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, path, context.Request.QueryString);
            proxyRequest.Headers.Remove("Cookie");
            var cookies = context.Request.Cookies.Where(cookie => !cookie.Key.Equals("PersonalServerManager.Auth", StringComparison.OrdinalIgnoreCase))
                .Select(cookie => $"{cookie.Key}={cookie.Value}");
            var cookieHeader = string.Join("; ", cookies);
            if (cookieHeader.Length > 0) proxyRequest.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            proxyRequest.Headers.Host = useUpstreamHost ? new Uri(destinationPrefix).Authority : context.Request.Host.Value;
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
            proxyRequest.Headers.Remove("X-Forwarded-Host");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
            proxyRequest.Headers.Remove("X-Forwarded-For");
            if (context.Connection.RemoteIpAddress is { } address)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
            proxyRequest.Headers.Remove("X-Forwarded-Prefix");
            if (stripPrefix) proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix);
            if (authorization is not null)
            {
                proxyRequest.Headers.Remove("Authorization");
                proxyRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
            }
        }
    }
}