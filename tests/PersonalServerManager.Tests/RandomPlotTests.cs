using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;
using PersonalServerManager.Components;
using PersonalServerManager.Management;
using PersonalServerManager.RandomPlot;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class RandomPlotTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("psm-randomplot-").FullName;

    [Fact]
    public async Task ExistingDataFileMustBeAbsolutePresentAndUnique()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "RandomPlotGenerator.exe"), "");
        var dataFile = Path.Combine(_folder, "data.json");
        var store = new JsonRandomPlotInstanceStore(Path.Combine(_folder, "instances.json"));
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Managed", new Uri("http://127.0.0.1:5243/"), _folder, DataFilePath: dataFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { DataFilePath = "relative.json" }));
        await File.WriteAllTextAsync(dataFile, "original");
        await store.SaveAsync(instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { Id = Guid.NewGuid(), Port = 5244, BaseUrl = new Uri("http://127.0.0.1:5244/") }));
        Assert.Equal("original", await File.ReadAllTextAsync(dataFile));
    }

    [Fact]
    public async Task StoreRoundTripsMultipleInstancesAndPreservesInvalidFile()
    {
        var path = Path.Combine(_folder, "instances.json");
        var store = new JsonRandomPlotInstanceStore(path);
        var external = new RandomPlotInstance(Guid.NewGuid(), " External ", new Uri("https://example.test/plots"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "RandomPlotGenerator.exe"), "");
        var managed = new RandomPlotInstance(Guid.NewGuid(), "Managed", new Uri("http://127.0.0.1:5243/"), _folder, 5243, true);
        await store.SaveAsync(external);
        await store.SaveAsync(managed);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(managed with { Id = Guid.NewGuid() }));
        var items = await store.GetAllAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal("External", items[0].Name);
        Assert.Equal("https://example.test/plots/mcp", items[0].McpUrl.AbsoluteUri);
        Assert.Equal($"http://127.0.0.1:5243/managed/randomplot/{managed.Id:D}/mcp", items[1].McpUrl.AbsoluteUri);
        await store.RemoveAsync(managed.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(managed));
        await File.WriteAllTextAsync(path, "not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(external));
        Assert.Equal("not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StoreRejectsUnsafeUrlsAndMissingPublishedExecutable()
    {
        var store = new JsonRandomPlotInstanceStore(Path.Combine(_folder, "instances.json"));
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Name", new Uri("http://127.0.0.1:5243/"), _folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { InstallationFolder = null, BaseUrl = new Uri("https://user:pass@example.test/") }));
        await File.WriteAllTextAsync(Path.Combine(_folder, "RandomPlotGenerator.exe"), "");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { BaseUrl = new Uri("http://example.test/") }));
    }

    [Fact]
    public async Task ClientRequestsGroupsWithEscapedQueryAndReadsValues()
    {
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var client = new RandomPlotClient(http);
        var instance = new RandomPlotInstance(Guid.NewGuid(), "External", new Uri("https://example.test/base/"));
        var groups = await client.GetGroupsAsync(instance, "space & slash/?");
        Assert.Equal("https://example.test/base/api/groups?query=space%20%26%20slash%2F%3F", handler.RequestUri!.AbsoluteUri);
        var group = Assert.Single(groups);
        Assert.Equal("Places", group.Name);
        Assert.True(group.IsFavorite);
        Assert.Equal("Castle", Assert.Single(group.Values).Value);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "http")]
    [InlineData(true, "success")]
    [InlineData(true, "http")]
    [InlineData(false, "invalid")]
    [InlineData(false, "json")]
    [InlineData(false, "content-type")]
    [InlineData(false, "timeout")]
    public async Task InstanceRowReportsApiAvailability(bool managed, string outcome)
    {
        var reachable = outcome == "success";
        var instance = new RandomPlotInstance(Guid.NewGuid(), "Test instance", new Uri("http://localhost:5243/"), managed ? _folder : null);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IRandomPlotInstanceStore>(new FixtureStore(instance));
        services.AddSingleton<IRandomPlotClient>(new FixtureClient(outcome));
        services.AddSingleton<IJSRuntime, FixtureJavaScript>();
        services.AddSingleton(new ManagedBackendService(null!, null!, NullLogger<ManagedBackendService>.Instance));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = new BackendSelection(),
                ["ChildContent"] = (RenderFragment)(builder => { builder.OpenComponent<RandomPlotInstances>(0); builder.CloseComponent(); })
            });
            return (await renderer.RenderComponentAsync<CascadingValue<BackendSelection>>(parameters)).ToHtmlString();
        });
        Assert.Contains(reachable ? "Available" : "Unavailable", html);
        Assert.Contains(reachable ? "backend-status-dot online" : "backend-status-dot offline", html);
        Assert.DoesNotContain("Unknown", html);
    }

    private sealed class FixtureStore(RandomPlotInstance instance) : IRandomPlotInstanceStore
    {
        public Task<IReadOnlyList<RandomPlotInstance>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RandomPlotInstance>>([instance]);
        public Task SaveAsync(RandomPlotInstance value, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(RandomPlotInstance value, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FixtureClient(string outcome) : IRandomPlotClient
    {
        public Task<IReadOnlyList<RandomPlotGroup>> GetGroupsAsync(RandomPlotInstance instance, string query, CancellationToken cancellationToken = default) =>
            outcome switch
            {
                "success" => Task.FromResult<IReadOnlyList<RandomPlotGroup>>([]),
                "invalid" => throw new InvalidDataException(),
                "json" => throw new JsonException(),
                "content-type" => throw new NotSupportedException(),
                "timeout" => throw new TaskCanceledException(),
                _ => throw new HttpRequestException("unreachable")
            };
    }

    private sealed class FixtureJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotImplementedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotImplementedException();
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[{\"name\":\"Places\",\"values\":[{\"value\":\"Castle\"}],\"isFavorite\":true}]", Encoding.UTF8, "application/json") });
        }
    }

    public void Dispose() => Directory.Delete(_folder, true);
}