using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.Hydrus;
using PersonalServerManager.Management;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace PersonalServerManager.Tests;

public sealed class ManagedBackendServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("psm-managed-").FullName;

    [Fact]
    public void UpdatesOnlyRootYamlPortAndPreservesOtherValues()
    {
        var result = ManagedBackendService.UpdateStashConfigPort("port: 9999\nplugins:\n  port: 42\npaths: [one, two]\nflag: true\ntext: '001'\n", 12345);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(result));
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        Assert.Equal("12345", ((YamlScalarNode)root.Children[new YamlScalarNode("port")]).Value);
        var nested = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("plugins")]);
        Assert.Equal("42", ((YamlScalarNode)nested.Children[new YamlScalarNode("port")]).Value);
        Assert.Equal(2, ((YamlSequenceNode)root.Children[new YamlScalarNode("paths")]).Children.Count);
        Assert.Equal("true", ((YamlScalarNode)root.Children[new YamlScalarNode("flag")]).Value);
        Assert.Equal("001", ((YamlScalarNode)root.Children[new YamlScalarNode("text")]).Value);
    }

    [Theory]
    [InlineData("[broken")]
    [InlineData("- sequence")]
    [InlineData("port: 1\n---\nport: 2")]
    public void RejectsInvalidYaml(string yaml) =>
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.UpdateStashConfigPort(yaml, 9999));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsInvalidPorts(int port) =>
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.UpdateStashConfigPort("{}", port));

    [Fact]
    public async Task HydrusMetadataRoundTripsAndNewManagedEntriesRequireKeys()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "hydrus_client.exe"), string.Empty);
        var store = CreateHydrusStore();
        var instance = new HydrusInstance(Guid.NewGuid(), "Hydrus", new Uri("http://localhost:45869/custom/"),
            "test-private-key", _folder, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { ApiKey = null }));
        await store.SaveAsync(instance);
        Assert.Equal(instance, Assert.Single(await store.GetAllAsync()));
        await store.SaveAsync(instance with { ApiKey = null });
        Assert.Equal(instance, Assert.Single(await store.GetAllAsync()));
        Assert.DoesNotContain("test-private-key", await File.ReadAllTextAsync(Path.Combine(_folder, "hydrus.json")));
    }

    [Fact]
    public async Task LegacyHydrusEndpointsRemainUnchanged()
    {
        var store = CreateHydrusStore();
        await File.WriteAllTextAsync(Path.Combine(_folder, "hydrus.json"),
            "[{\"id\":\"b5f6e5f1-b07c-49cc-a9a2-236713903511\",\"name\":\"Legacy\",\"apiEndpoint\":\"https://example.test/api/\"}]");
        var legacy = Assert.Single(await store.GetAllAsync());
        Assert.False(legacy.IsManaged);
        Assert.False(legacy.StartWithApp);
        await store.SaveAsync(legacy);
        Assert.Equal(legacy, Assert.Single(await store.GetAllAsync()));
    }

    [Fact]
    public async Task UnknownAndUnmanagedInstancesExposeSafeLifecycleErrors()
    {
        var stash = CreateStashStore();
        using var service = new ManagedBackendService(stash, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        var id = Guid.NewGuid();
        await service.StartInstanceAsync(id);
        Assert.False(service.GetStatus(id).IsRunning);
        Assert.Contains("No managed backend", service.GetStatus(id).Message);
        await stash.SaveAsync(new(id, "Remote", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), "secret"));
        await service.StartInstanceAsync(id);
        Assert.DoesNotContain("secret", service.GetStatus(id).Message);
        await service.StopInstanceAsync(id);
        Assert.Contains("no PSM-owned process", service.GetStatus(id).Message);
    }

    [Fact]
    public async Task MissingExecutableAndMismatchedStashEndpointAreRejected()
    {
        var store = CreateStashStore();
        var instance = new StashInstance(Guid.NewGuid(), "Stash", new Uri("http://127.0.0.1:9999/graphql"),
            new Uri("http://127.0.0.1:9999/"), null, _folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance));
        await File.WriteAllTextAsync(Path.Combine(_folder, "stash-win.exe"), string.Empty);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { Port = 9998 }));
        await store.SaveAsync(instance);
        File.Delete(Path.Combine(_folder, "stash-win.exe"));
        using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        await service.StartInstanceAsync(instance.Id);
        Assert.Contains("stash-win.exe", service.GetStatus(instance.Id).Message);
        Assert.False(File.Exists(Path.Combine(_folder, "config.yml")));
    }

    [Fact]
    public async Task OccupiedPortDoesNotLaunchOrChangeYamlOrStopExternalListener()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            await File.WriteAllTextAsync(Path.Combine(_folder, "stash-win.exe"), string.Empty);
            const string original = "port: 9999\nother: preserved\n";
            await File.WriteAllTextAsync(Path.Combine(_folder, "config.yml"), original);
            var store = CreateStashStore();
            var instance = new StashInstance(Guid.NewGuid(), "Stash", new Uri($"http://127.0.0.1:{port}/graphql"),
                new Uri($"http://127.0.0.1:{port}/"), null, _folder, port);
            await store.SaveAsync(instance);
            using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
            await Task.WhenAll(service.StartInstanceAsync(instance.Id), service.StartInstanceAsync(instance.Id));
            Assert.Contains("already in use", service.GetStatus(instance.Id).Message);
            Assert.False(service.GetStatus(instance.Id).IsRunning);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_folder, "config.yml")));
            await service.StopInstanceAsync(instance.Id);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task HostAutostartErrorsAreVisibleAndShutdownRejectsNewStarts()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            await File.WriteAllTextAsync(Path.Combine(_folder, "stash-win.exe"), string.Empty);
            var store = CreateStashStore();
            var instance = new StashInstance(Guid.NewGuid(), "Stash", new Uri($"http://127.0.0.1:{port}/graphql"),
                new Uri($"http://127.0.0.1:{port}/"), null, _folder, port, true);
            await store.SaveAsync(instance);
            using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
            await service.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!service.GetStatus(instance.Id).Message.Contains("already in use"))
                await Task.Delay(10, timeout.Token);
            await service.StopAsync(CancellationToken.None);
            await service.StartInstanceAsync(instance.Id);
            Assert.Contains("shutting down", service.GetStatus(instance.Id).Message);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void SwarmUIManagedValidationRequiresBuiltExecutableAndLoopbackUrl()
    {
        var instance = new SwarmUIInstance(Guid.NewGuid(), "Swarm", new Uri("http://127.0.0.1:7801/"),
            SwarmUIInstance.DefaultSearchPaths, _folder);
        var missing = Assert.Throws<InvalidDataException>(() => ManagedBackendService.ValidateSwarmUIInstance(instance));
        Assert.Contains("SwarmUI.exe", missing.Message);
        var binaries = Directory.CreateDirectory(Path.Combine(_folder, "src", "bin", "live_release"));
        File.WriteAllText(Path.Combine(binaries.FullName, "SwarmUI.exe"), string.Empty);

        ManagedBackendService.ValidateSwarmUIInstance(instance);
        ManagedBackendService.ValidateSwarmUIInstance(instance with { InstallationFolder = null, BaseUrl = new Uri("http://example.test/") });
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.ValidateSwarmUIInstance(instance with { InstallationFolder = "relative\\swarm" }));
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.ValidateSwarmUIInstance(instance with { BaseUrl = new Uri("http://localhost:7801/") }));
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.ValidateSwarmUIInstance(instance with { Port = 7802 }));
        Assert.Throws<InvalidDataException>(() => ManagedBackendService.ValidateSwarmUIInstance(instance with { Port = 0, BaseUrl = new Uri("http://127.0.0.1/") }));
    }

    [Fact]
    public async Task ManagedSwarmUIOnOccupiedPortIsNotLaunched()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var binaries = Directory.CreateDirectory(Path.Combine(_folder, "src", "bin", "live_release"));
            await File.WriteAllTextAsync(Path.Combine(binaries.FullName, "SwarmUI.exe"), string.Empty);
            var swarm = new JsonSwarmUIInstanceStore(Path.Combine(_folder, "swarmui.json"));
            var instance = new SwarmUIInstance(Guid.NewGuid(), "Swarm", new Uri($"http://127.0.0.1:{port}/"),
                SwarmUIInstance.DefaultSearchPaths, _folder, port);
            await swarm.SaveAsync(instance);
            using var service = new ManagedBackendService(CreateStashStore(), CreateHydrusStore(),
                NullLogger<ManagedBackendService>.Instance, swarm);
            await service.StartInstanceAsync(instance.Id);
            Assert.Contains("already in use", service.GetStatus(instance.Id).Message);
            Assert.False(service.GetStatus(instance.Id).IsRunning);
        }
        finally { listener.Stop(); }
    }

    private JsonStashInstanceStore CreateStashStore() => new(Path.Combine(_folder, "stash.json"),
        new EphemeralDataProtectionProvider().CreateProtector("test"));
    private JsonHydrusInstanceStore CreateHydrusStore() => new(Path.Combine(_folder, "hydrus.json"),
        new EphemeralDataProtectionProvider().CreateProtector("test"));
    public void Dispose() => Directory.Delete(_folder, true);
}