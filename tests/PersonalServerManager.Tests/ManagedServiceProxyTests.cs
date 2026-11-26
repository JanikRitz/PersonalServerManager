using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Management;
using PersonalServerManager.RandomPlot;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class ManagedServiceProxyTests
{
    [Fact]
    public async Task ManagedStashRequiresAuthenticationAndForwardsOnlyManagedInstances()
    {
        var upstreamRequests = 0;
        string? randomSocketHost = null;
        string? randomSocketForwardedHost = null;
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.Logging.ClearProviders();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = upstreamBuilder.Build();
        upstream.UseWebSockets();
        upstream.Run(async context =>
        {
            Interlocked.Increment(ref upstreamRequests);
            if (context.WebSockets.IsWebSocketRequest)
            {
                if (context.Request.Path.Value!.StartsWith("/managed/randomplot/", StringComparison.Ordinal))
                {
                    randomSocketHost = context.Request.Host.Value;
                    randomSocketForwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[1024];
                var result = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage,
                    context.RequestAborted);
                await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
                return;
            }

            await context.Response.WriteAsJsonAsync(new UpstreamRequest(
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                context.Request.Headers["X-Forwarded-Prefix"].ToString(),
                context.Request.Headers.Cookie.ToString(),
                context.Request.Host.Value ?? "",
                context.Request.Headers["X-Forwarded-Host"].ToString(),
                context.Request.Headers["X-Forwarded-Proto"].ToString(),
                context.Request.Headers["X-Forwarded-For"].ToString()));
        });
        await upstream.StartAsync();
        var upstreamAddress = new Uri(upstream.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        var managed = new StashInstance(Guid.NewGuid(), "Managed", new Uri(upstreamAddress, "/graphql"),
            upstreamAddress, null, InstallationFolder: "dummy", Port: upstreamAddress.Port);
        var external = managed with { Id = Guid.NewGuid(), Name = "External", InstallationFolder = null };
        var libraries = new FixtureLocalLibraryStore();
        var randomFolder = Directory.CreateTempSubdirectory("psm-randomplot-proxy-");
        File.WriteAllText(Path.Combine(randomFolder.FullName, "RandomPlotGenerator.exe"), "");
        var randomStore = new JsonRandomPlotInstanceStore(Path.Combine(randomFolder.FullName, "instances.json"));
        var random = new RandomPlotInstance(Guid.NewGuid(), "Random", upstreamAddress, randomFolder.FullName, upstreamAddress.Port);
        var externalRandom = new RandomPlotInstance(Guid.NewGuid(), "External", upstreamAddress);
        await randomStore.SaveAsync(random);
        await randomStore.SaveAsync(externalRandom);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IStashInstanceStore>(new FixtureStashStore(managed, external));
        builder.Services.AddSingleton<ILocalLibraryStore>(libraries);
        builder.Services.AddSingleton<IRandomPlotInstanceStore>(randomStore);
        builder.Services.AddSingleton(serviceProvider => new ManagedCalibreServer(
            Path.Combine(Path.GetTempPath(), $"psm-calibre-{Guid.NewGuid():N}.json"), libraries,
            serviceProvider.GetRequiredService<ILogger<ManagedCalibreServer>>()));
        builder.Services.AddSingleton(serviceProvider => new ComicCompanionService(
            Path.Combine(Path.GetTempPath(), $"psm-comic-{Guid.NewGuid():N}.json"),
            serviceProvider.GetRequiredService<ILogger<ComicCompanionService>>()));
        builder.Services.AddHttpForwarder();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapManagedServiceProxy();
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        var managedPath = $"/managed/stash/{managed.Id:D}";
        using var unauthorized = await http.GetAsync($"{managedPath}/scenes/1?x=y");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(0, Volatile.Read(ref upstreamRequests));

        http.DefaultRequestHeaders.Add("X-Fixture-Authorization", "yes");
        http.DefaultRequestHeaders.Add("Cookie", "PersonalServerManager.Auth=secret-fixture; upstream=value");
        http.DefaultRequestHeaders.Host = "public.example:8443";
        http.DefaultRequestHeaders.Add("X-Forwarded-Host", "spoofed.example");
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");
        http.DefaultRequestHeaders.Add("X-Forwarded-Prefix", "/spoofed");
        using var response = await http.GetAsync($"{managedPath}/scenes/1?x=y");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = await response.Content.ReadFromJsonAsync<UpstreamRequest>();
        Assert.NotNull(forwarded);
        Assert.Equal("/scenes/1", forwarded.Path);
        Assert.Equal("?x=y", forwarded.Query);
        Assert.Equal(managedPath, forwarded.ForwardedPrefix);
        Assert.Equal("public.example:8443", forwarded.Host);
        Assert.Equal("public.example:8443", forwarded.ForwardedHost);
        Assert.Equal("http", forwarded.ForwardedProto);
        Assert.Equal(IPAddress.Loopback.ToString(), forwarded.ForwardedFor);
        Assert.DoesNotContain("PersonalServerManager.Auth", forwarded.Cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-fixture", forwarded.Cookie, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref upstreamRequests));

        using var externalResponse = await http.GetAsync($"/managed/stash/{external.Id:D}/scenes/1?x=y");
        Assert.Equal(HttpStatusCode.NotFound, externalResponse.StatusCode);
        using var missingResponse = await http.GetAsync($"/managed/stash/{Guid.NewGuid():D}/scenes/1?x=y");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(1, Volatile.Read(ref upstreamRequests));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("X-Fixture-Authorization", "yes");
        var webSocketAddress = new UriBuilder(http.BaseAddress!)
        {
            Scheme = "ws",
            Path = $"{managedPath}/echo"
        }.Uri;
        await webSocket.ConnectAsync(webSocketAddress, cancellation.Token);
        var message = Encoding.UTF8.GetBytes("managed stash echo");
        await webSocket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, cancellation.Token);
        var echo = new byte[1024];
        var echoResult = await webSocket.ReceiveAsync(echo.AsMemory(), cancellation.Token);
        Assert.Equal(WebSocketMessageType.Text, echoResult.MessageType);
        Assert.True(echoResult.EndOfMessage);
        Assert.Equal("managed stash echo", Encoding.UTF8.GetString(echo, 0, echoResult.Count));
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);
        Assert.Equal(2, Volatile.Read(ref upstreamRequests));

        var randomPath = $"/managed/randomplot/{random.Id:D}";
        http.DefaultRequestHeaders.Remove("X-Fixture-Authorization");
        using var unauthorizedRandom = await http.GetAsync($"{randomPath}/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedRandom.StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-Authorization", "yes");
        using var randomResponse = await http.GetAsync($"{randomPath}/_framework/blazor.web.js?x=y");
        var randomRequest = await randomResponse.Content.ReadFromJsonAsync<UpstreamRequest>();
        Assert.Equal(HttpStatusCode.OK, randomResponse.StatusCode);
        Assert.Equal($"{randomPath}/_framework/blazor.web.js", randomRequest!.Path);
        Assert.Equal("?x=y", randomRequest.Query);
        Assert.Equal($"127.0.0.1:{upstreamAddress.Port}", randomRequest.Host);
        Assert.Equal("public.example:8443", randomRequest.ForwardedHost);
        Assert.DoesNotContain("PersonalServerManager.Auth", randomRequest.Cookie, StringComparison.OrdinalIgnoreCase);
        using var externalRandomResponse = await http.GetAsync($"/managed/randomplot/{externalRandom.Id:D}/");
        Assert.Equal(HttpStatusCode.NotFound, externalRandomResponse.StatusCode);
        using var randomSocket = new ClientWebSocket();
        randomSocket.Options.SetRequestHeader("X-Fixture-Authorization", "yes");
        randomSocket.Options.SetRequestHeader("Host", "public.example:8443");
        await randomSocket.ConnectAsync(new UriBuilder(http.BaseAddress!) { Scheme = "ws", Path = $"{randomPath}/_blazor" }.Uri, cancellation.Token);
        await randomSocket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, cancellation.Token);
        var randomEcho = await randomSocket.ReceiveAsync(echo.AsMemory(), cancellation.Token);
        Assert.Equal("managed stash echo", Encoding.UTF8.GetString(echo, 0, randomEcho.Count));
        Assert.Equal($"127.0.0.1:{upstreamAddress.Port}", randomSocketHost);
        Assert.Equal("public.example:8443", randomSocketForwardedHost);
        await randomSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation.Token);

        var comicFolder = Directory.CreateTempSubdirectory("psm-comic-proxy-");
        try
        {
            File.WriteAllText(Path.Combine(comicFolder.FullName, "HydrusComicCompanion.csproj"), "");
            var comics = app.Services.GetRequiredService<ComicCompanionService>();
            var managedComic = await comics.SaveSettingsAsync(new(upstreamAddress, InstallationFolder: comicFolder.FullName)
                { Id = Guid.NewGuid(), Name = "Managed Comic" });
            var externalComic = await comics.SaveSettingsAsync(new(upstreamAddress)
                { Id = Guid.NewGuid(), Name = "External Comic" });
            var comicPath = $"/managed/comiccompanion/{managedComic.Id:D}";
            http.DefaultRequestHeaders.Remove("X-Fixture-Authorization");
            using var unauthorizedComic = await http.GetAsync($"{comicPath}/read/1/2/3");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedComic.StatusCode);
            http.DefaultRequestHeaders.Add("X-Fixture-Authorization", "yes");
            using var comicResponse = await http.GetAsync($"{comicPath}/read/1/2/3?x=y");
            Assert.Equal(HttpStatusCode.OK, comicResponse.StatusCode);
            var comicRequest = await comicResponse.Content.ReadFromJsonAsync<UpstreamRequest>();
            Assert.NotNull(comicRequest);
            Assert.Equal($"{comicPath}/read/1/2/3", comicRequest.Path);
            Assert.Equal("?x=y", comicRequest.Query);
            Assert.Equal("", comicRequest.ForwardedPrefix);
            Assert.Equal("public.example:8443", comicRequest.Host);
            Assert.DoesNotContain("PersonalServerManager.Auth", comicRequest.Cookie, StringComparison.OrdinalIgnoreCase);
            using var externalComicResponse = await http.GetAsync($"/managed/comiccompanion/{externalComic.Id:D}/");
            Assert.Equal(HttpStatusCode.NotFound, externalComicResponse.StatusCode);
            using var missingComicResponse = await http.GetAsync($"/managed/comiccompanion/{Guid.NewGuid():D}/");
            Assert.Equal(HttpStatusCode.NotFound, missingComicResponse.StatusCode);
            Assert.Equal(5, Volatile.Read(ref upstreamRequests));
        }
        finally
        {
            comicFolder.Delete(recursive: true);
            randomFolder.Delete(recursive: true);
        }
    }

    private sealed record UpstreamRequest(string Path, string Query, string ForwardedPrefix, string Cookie,
        string Host, string ForwardedHost, string ForwardedProto, string ForwardedFor);

    private sealed class FixtureStashStore(params StashInstance[] instances) : IStashInstanceStore
    {
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StashInstance>>(instances);

        public Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixtureLocalLibraryStore : ILocalLibraryStore
    {
        public Task<IReadOnlyList<LocalLibraryInstance>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalLibraryInstance>>([]);

        public Task SaveAsync(LocalLibraryInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers["X-Fixture-Authorization"] == "yes"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "fixture")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}