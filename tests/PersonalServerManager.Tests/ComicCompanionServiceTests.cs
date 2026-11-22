using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PersonalServerManager.Management;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class ComicCompanionServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("psm-comic-").FullName;

    [Theory]
    [InlineData(HttpStatusCode.OK, "[]", true)]
    [InlineData(HttpStatusCode.OK, "[{\"id\":1,\"title\":\"Comic\"}]", true)]
    [InlineData(HttpStatusCode.OK, "<html>Not the API</html>", false)]
    [InlineData(HttpStatusCode.OK, "[{}]", false)]
    [InlineData(HttpStatusCode.Unauthorized, "[]", false)]
    [InlineData(HttpStatusCode.Redirect, "[]", false)]
    public async Task CatalogProbeRequiresAValidCatalogResponse(HttpStatusCode status, string body, bool available)
    {
        using var client = new HttpClient(new ProbeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example.test/catalog/api/manager/comics?q=&limit=1&sort=TitleAscending", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }));
        var probe = new ComicCompanionCatalogProbe(client);
        var settings = new ComicCompanionSettings(new("https://api.example.test/catalog/"), new("https://browser.example.test/"));
        Assert.Equal(available, await probe.IsAvailableAsync(settings, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogProbeRejectsOversizedBodies()
    {
        using var client = new HttpClient(new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]" + new string(' ', 64 * 1024))
        })));
        var probe = new ComicCompanionCatalogProbe(client);
        Assert.False(await probe.IsAvailableAsync(new(new("https://api.example.test/")), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogProbeDistinguishesItsDeadlineFromCallerCancellation(bool cancelCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new ProbeHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The probe did not cancel the request.");
        }));
        var probe = new ComicCompanionCatalogProbe(client);
        using var cancellation = new CancellationTokenSource();
        var result = probe.IsAvailableAsync(new(new("https://api.example.test/")), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelCaller)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task ExternalSettingsRoundTripAndDoNotEnableManagedFeatures()
    {
        using var host = BuildHost();
        var service = host.Services.GetRequiredService<ComicCompanionService>();
        Assert.Same(service, Assert.Single(host.Services.GetServices<IHostedService>()));
        var defaults = await service.GetSettingsAsync();
        Assert.Null(defaults.ApiUrl);
        Assert.False(defaults.AutomaticSync);
        Assert.Equal(60, defaults.SyncIntervalMinutes);

        await service.SaveSettingsAsync(new(new("https://api.example.test/comics"), new("https://web.example.test"),
            StartWithApp: true, AutomaticSync: true));
        using var secondHost = BuildHost();
        var settings = await secondHost.Services.GetRequiredService<ComicCompanionService>().GetSettingsAsync();
        Assert.Equal(new Uri("https://api.example.test/comics/"), settings.ApiUrl);
        Assert.Equal(new Uri("https://web.example.test/"), settings.EffectiveBrowserUrl);
        Assert.False(settings.IsManaged);
        Assert.False(settings.StartWithApp);
        Assert.False(settings.AutomaticSync);
    }

    [Fact]
    public async Task LegacyConfigurationKeepsItsIdWhenAddingEditingAndRemovingInstances()
    {
        var settingsPath = Path.Combine(_folder, "comic-companion.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"ApiUrl":"http://192.168.1.20:5053", "BrowserUrl":"https://comic.example.test"}
            """);
        using var host = BuildHost();
        var service = host.Services.GetRequiredService<ComicCompanionService>();
        var legacy = Assert.Single(await service.GetAllAsync());
        Assert.Equal(Guid.Parse("7926576f-37c3-462d-a59e-2a6118ca439b"), legacy.Id);
        Assert.Equal("Comic Companion", legacy.Name);
        Assert.Equal(new Uri("https://comic.example.test/"), legacy.EffectiveBrowserUrl);

        var added = await service.SaveSettingsAsync(new(new("https://other.example.test"))
        {
            Id = Guid.NewGuid(), Name = "  Second library  "
        });
        Assert.Equal("Second library", added.Name);
        await service.SaveSettingsAsync(legacy with { Name = "LAN library" });

        using var reloadedHost = BuildHost();
        var reloaded = reloadedHost.Services.GetRequiredService<ComicCompanionService>();
        Assert.Equal(new[] { legacy with { Name = "LAN library" }, added }, await reloaded.GetAllAsync());
        await reloaded.RemoveSettingsAsync(legacy.Id);
        Assert.Equal(added, Assert.Single(await service.GetAllAsync()));
        await reloaded.RemoveSettingsAsync(added.Id);
        Assert.Empty(await service.GetAllAsync());
    }

    [Theory]
    [InlineData("file:///C:/comics")]
    [InlineData("http://user:password@example.test/")]
    [InlineData("https://example.test/?token=secret")]
    [InlineData("https://example.test/#fragment")]
    public async Task RejectsUnsafeBaseUrls(string url)
    {
        using var host = BuildHost();
        var service = host.Services.GetRequiredService<ComicCompanionService>();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(new(new(url))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(new(new("https://example.test"), new(url))));
    }

    [Fact]
    public async Task ManagedConfigurationRequiresLocalUrlAndKnownArtifact()
    {
        using var host = BuildHost();
        var service = host.Services.GetRequiredService<ComicCompanionService>();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(new(new("http://0.0.0.0:5053"), InstallationFolder: _folder)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(new(new("http://127.0.0.1:5053"), InstallationFolder: _folder)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(new(new("http://127.0.0.1:5053"), SyncIntervalMinutes: 0)));
    }

    [Fact]
    public void ManagedBrowserLinksUseInstancePathAndIgnoreConfiguredBrowserPrefix()
    {
        var id = Guid.NewGuid();
        var settings = new ComicCompanionSettings(new("http://127.0.0.1:5053/"),
            new("https://reader.example.test/old-path/"), InstallationFolder: _folder) { Id = id };
        Assert.Equal($"/managed/comiccompanion/{id:D}/", settings.BrowserLink(settings.EffectiveBrowserUrl!).OriginalString);
        Assert.Equal($"/managed/comiccompanion/{id:D}/read/11/42/1?view=full#page",
            settings.BrowserLink(new Uri(settings.EffectiveBrowserUrl!, "read/11/42/1?view=full#page")).OriginalString);
        var external = settings with { InstallationFolder = null };
        Assert.Equal("https://reader.example.test/old-path/read/11/42/1",
            external.BrowserLink(new Uri(external.EffectiveBrowserUrl!, "read/11/42/1")).AbsoluteUri);
    }

    [Fact]
    public void SourceAndPublishedLaunchesUseTheInstallFolderAndProcessLocalSync()
    {
        var project = Path.Combine(_folder, "HydrusComicCompanion.csproj");
        File.WriteAllText(project, "");
        var settings = new ComicCompanionSettings(new("http://127.0.0.1:5053"), InstallationFolder: _folder);
        var source = ComicCompanionService.CreateStartInfo(settings);
        Assert.Equal(_folder, source.WorkingDirectory);
        Assert.False(source.UseShellExecute);
        Assert.Equal(new[] { "run", "--project", project, "--no-launch-profile", "--", "--urls", "http://127.0.0.1:5053" }, source.ArgumentList);
        Assert.Equal($"/managed/comiccompanion/{ComicCompanionService.LegacyInstanceId:D}", source.Environment["PSM_COMIC_PATH_BASE"]);
        Assert.False(source.Environment.ContainsKey("PSM_COMIC_SYNC_MINUTES"));
        var lan = ComicCompanionService.CreateStartInfo(settings with { ApiUrl = new("http://192.168.1.20:5053/") });
        Assert.Equal("http://192.168.1.20:5053", lan.ArgumentList[^1]);

        var assembly = Path.Combine(_folder, "HydrusComicCompanion.dll");
        File.WriteAllText(assembly, "");
        File.WriteAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json"), "");
        var published = ComicCompanionService.CreateStartInfo(settings with { AutomaticSync = true });
        Assert.Equal(new[] { assembly, "--urls", "http://127.0.0.1:5053" }, published.ArgumentList);
        Assert.Equal("60", published.Environment["PSM_COMIC_SYNC_MINUTES"]);

        var executable = Path.Combine(_folder, "HydrusComicCompanion.exe");
        File.WriteAllText(executable, "");
        var appHost = ComicCompanionService.CreateStartInfo(settings with { AutomaticSync = true, SyncIntervalMinutes = 15 });
        Assert.Equal(executable, appHost.FileName);
        Assert.Equal("15", appHost.Environment["PSM_COMIC_SYNC_MINUTES"]);
    }

    [Fact]
    public async Task OccupiedPortIsNotAdoptedByAutostartStopRestartOrHostShutdown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(_folder, "HydrusComicCompanion.csproj"), "");
        using var host = BuildHost();
        var service = host.Services.GetRequiredService<ComicCompanionService>();
        await service.SaveSettingsAsync(new(new($"http://127.0.0.1:{port}"), InstallationFolder: _folder, StartWithApp: true));
        var other = await service.SaveSettingsAsync(new(new($"http://127.0.0.1:{port}"), InstallationFolder: _folder,
            AutomaticSync: true, SyncIntervalMinutes: 15) { Id = Guid.NewGuid(), Name = "Other library" });

        await host.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!service.GetStatus().Message.Contains("already in use", StringComparison.Ordinal))
            await Task.Delay(20, timeout.Token);

        Assert.False(service.GetStatus().IsRunning);
    Assert.Equal("No PSM-owned process", service.GetStatus(other.Id).Message);
    await service.StartInstanceAsync(other.Id);
    Assert.Contains("already in use", service.GetStatus(other.Id).Message);
    Assert.False(service.GetStatus(other.Id).IsRunning);
    await service.StopInstanceAsync(other.Id);
    await service.RestartInstanceAsync(other.Id);
    await service.RemoveSettingsAsync(other.Id);
    Assert.Contains("already in use", service.GetStatus().Message);
    Assert.Equal(60, (await service.GetSettingsAsync()).SyncIntervalMinutes);
    Assert.True(other.AutomaticSync);
    Assert.Equal(15, other.SyncIntervalMinutes);
        await service.StopInstanceAsync();
        await service.RestartInstanceAsync();
        await host.StopAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddComicCompanion(_folder);
        return builder.Build();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}