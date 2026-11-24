using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    [Theory]
    [InlineData(false, "127.0.0.1")]
    [InlineData(true, "0.0.0.0")]
    public async Task ManagedSwarmUILaunchUsesConfiguredHost(bool listenOnLocalNetwork, string expectedHost)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var binaries = Directory.CreateDirectory(Path.Combine(_folder, "src", "bin", "live_release"));
        await File.WriteAllTextAsync(Path.Combine(binaries.FullName, "SwarmUI.exe"), string.Empty);
        var swarm = new JsonSwarmUIInstanceStore(Path.Combine(_folder, "swarmui.json"));
        var instance = new SwarmUIInstance(Guid.NewGuid(), "Swarm", new Uri($"http://127.0.0.1:{port}/"),
            SwarmUIInstance.DefaultSearchPaths, _folder, port, ListenOnLocalNetwork: listenOnLocalNetwork);
        await swarm.SaveAsync(instance);
        ProcessStartInfo? launched = null;
        using var service = new ManagedBackendService(CreateStashStore(), CreateHydrusStore(),
            NullLogger<ManagedBackendService>.Instance, swarm, info => { launched = info; return null; });
        await service.StartInstanceAsync(instance.Id);
        Assert.NotNull(launched);
        Assert.Equal(["--launch_mode", "none", "--host", expectedHost, "--port", port.ToString()], launched.ArgumentList);
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

    [Fact]
    public async Task RemovalSerializesQueuedStartAndRestart()
    {
        var store = new ControlledStashStore(CreateStashStore());
        var instance = new StashInstance(Guid.NewGuid(), "Remote", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), null);
        await store.SaveAsync(instance);
        using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        var remove = service.RemoveInstanceAsync("Stash", instance.Id);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var start = service.StartInstanceAsync(instance.Id);
        var restart = service.RestartInstanceAsync(instance.Id);
        try
        {
            Assert.False(start.IsCompleted);
            Assert.False(restart.IsCompleted);
        }
        finally { store.Continue.TrySetResult(); }
        await Task.WhenAll(remove, start, restart).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await store.GetAllAsync());
        Assert.Contains("No managed backend", service.GetStatus(instance.Id).Message);
    }

    [Fact]
    public async Task RemovalWriteFailureRetainsConfiguration()
    {
        var store = new ControlledStashStore(CreateStashStore()) { FailRemoval = true };
        store.Continue.TrySetResult();
        var instance = new StashInstance(Guid.NewGuid(), "Remote", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), null);
        await store.SaveAsync(instance);
        using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        await Assert.ThrowsAsync<IOException>(() => service.RemoveInstanceAsync("Stash", instance.Id));
        Assert.Equal(instance.Id, Assert.Single(await store.GetAllAsync()).Id);
    }

    [Fact]
    public async Task RemovalRoutesToTheRequestedBackendAndRejectsSharedIds()
    {
        var stash = CreateStashStore();
        var hydrus = CreateHydrusStore();
        var swarm = new JsonSwarmUIInstanceStore(Path.Combine(_folder, "swarmui.json"));
        var stashId = Guid.NewGuid();
        var hydrusId = Guid.NewGuid();
        var swarmId = Guid.NewGuid();
        await stash.SaveAsync(new(stashId, "Stash", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), null));
        await hydrus.SaveAsync(new(hydrusId, "Hydrus", new Uri("https://example.test/"), null));
        await swarm.SaveAsync(new(swarmId, "Swarm", new Uri("https://example.test/"), SwarmUIInstance.DefaultSearchPaths));
        using var service = new ManagedBackendService(stash, hydrus, NullLogger<ManagedBackendService>.Instance, swarm);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveInstanceAsync("Hydrus", stashId));
        await hydrus.SaveAsync(new(stashId, "Shared", new Uri("https://example.test/"), null));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveInstanceAsync("Stash", stashId));
        await hydrus.RemoveAsync(stashId);
        await service.RemoveInstanceAsync("Hydrus", hydrusId);
        await service.RemoveInstanceAsync("SwarmUI", swarmId);
        await service.RemoveInstanceAsync("Stash", stashId);
        Assert.Empty(await stash.GetAllAsync());
        Assert.Empty(await hydrus.GetAllAsync());
        Assert.Empty(await swarm.GetAllAsync());
    }

    [Theory]
    [InlineData("Stash")]
    [InlineData("Hydrus")]
    [InlineData("SwarmUI")]
    public async Task EditQueuedDuringStartRechecksOwnedProcessEvenForExternalDraft(string backend)
    {
        var stash = CreateStashStore();
        var hydrus = CreateHydrusStore();
        var swarm = new JsonSwarmUIInstanceStore(Path.Combine(_folder, "swarmui.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var id = Guid.NewGuid();
        var stashInstance = new StashInstance(id, "Original", new Uri($"http://127.0.0.1:{port}/graphql"),
            new Uri($"http://127.0.0.1:{port}/"), null, _folder, port);
        var hydrusInstance = new HydrusInstance(id, "Original", new Uri($"http://127.0.0.1:{port}/"), "key", _folder);
        var swarmInstance = new SwarmUIInstance(id, "Original", new Uri($"http://127.0.0.1:{port}/"),
            SwarmUIInstance.DefaultSearchPaths, _folder, port);
        await File.WriteAllTextAsync(Path.Combine(_folder, "stash-win.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(_folder, "hydrus_client.exe"), "fixture");
        var binaries = Directory.CreateDirectory(Path.Combine(_folder, "src", "bin", "live_release"));
        await File.WriteAllTextAsync(Path.Combine(binaries.FullName, "SwarmUI.exe"), "fixture");
        if (backend == "Stash") await stash.SaveAsync(stashInstance);
        else if (backend == "Hydrus") await hydrus.SaveAsync(hydrusInstance);
        else await swarm.SaveAsync(swarmInstance);

        using var proceed = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        using var service = new ManagedBackendService(stash, hydrus, NullLogger<ManagedBackendService>.Instance, swarm, _ =>
        {
            entered.TrySetResult();
            if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Launch timed out.");
            return process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p fixture=")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
        });
        var start = Task.Run(() => service.StartInstanceAsync(id));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task EditAsync() => backend switch
            {
                "Stash" => service.UpdateInstanceAsync(stashInstance with { Name = "Edited", InstallationFolder = null }),
                "Hydrus" => service.UpdateInstanceAsync(hydrusInstance with { Name = "Edited", InstallationFolder = null }),
                _ => service.UpdateInstanceAsync(swarmInstance with { Name = "Edited", InstallationFolder = null })
            };
            var edit = EditAsync();
            Assert.False(edit.IsCompleted);
            proceed.Set();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => edit);
            Assert.Equal($"Stop {backend} before editing its configuration.", error.Message);
            Assert.True(service.GetStatus(id).IsRunning);
            var name = backend switch
            {
                "Stash" => Assert.Single(await stash.GetAllAsync()).Name,
                "Hydrus" => Assert.Single(await hydrus.GetAllAsync()).Name,
                _ => Assert.Single(await swarm.GetAllAsync()).Name
            };
            Assert.Equal("Original", name);
            Assert.NotNull(process);
            process.Kill(true);
            await process.WaitForExitAsync();
            process = null;
            await EditAsync();
            await service.RemoveInstanceAsync(backend, id);
            Assert.Contains("no longer exists", (await Assert.ThrowsAsync<InvalidDataException>(EditAsync)).Message);
        }
        finally
        {
            proceed.Set();
            await start;
            if (process is not null && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await service.StopInstanceAsync(id);
        }
    }

    [Fact]
    public async Task EditQueuedBehindRemovalDoesNotRecreateInstance()
    {
        var store = new ControlledStashStore(CreateStashStore());
        var instance = new StashInstance(Guid.NewGuid(), "Remote", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), null);
        await store.SaveAsync(instance);
        using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        var remove = service.RemoveInstanceAsync("Stash", instance.Id);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var edit = service.UpdateInstanceAsync(instance with { Name = "Edited" });
        try { Assert.False(edit.IsCompleted); }
        finally { store.Continue.TrySetResult(); }
        await remove.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("no longer exists", (await Assert.ThrowsAsync<InvalidDataException>(() => edit)).Message);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task RemovalWaitsForUpdateAndRemovesUpdatedInstance()
    {
        var store = new ControlledStashStore(CreateStashStore()) { PauseUpdate = true };
        var instance = new StashInstance(Guid.NewGuid(), "Remote", new Uri("https://example.test/graphql"), new Uri("https://example.test/"), null);
        await store.SaveAsync(instance);
        using var service = new ManagedBackendService(store, CreateHydrusStore(), NullLogger<ManagedBackendService>.Instance);
        var edit = service.UpdateInstanceAsync(instance with { Name = "Edited" });
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = service.RemoveInstanceAsync("Stash", instance.Id);
        try { Assert.False(remove.IsCompleted); }
        finally { store.Continue.TrySetResult(); }
        await Task.WhenAll(edit, remove).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await store.GetAllAsync());
    }

    private sealed class ControlledStashStore(IStashInstanceStore inner) : IStashInstanceStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailRemoval { get; init; }
        public bool PauseUpdate { get; init; }
        public Task<IReadOnlyList<StashInstance>> GetAllAsync(CancellationToken cancellationToken = default) => inner.GetAllAsync(cancellationToken);
        public Task SaveAsync(StashInstance instance, CancellationToken cancellationToken = default) => inner.SaveAsync(instance, cancellationToken);
        public async Task UpdateAsync(StashInstance instance, CancellationToken cancellationToken = default)
        {
            if (PauseUpdate)
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            await inner.UpdateAsync(instance, cancellationToken);
        }
        public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            if (FailRemoval) throw new IOException("Simulated configuration write failure.");
            await inner.RemoveAsync(id, cancellationToken);
        }
    }

    private JsonStashInstanceStore CreateStashStore() => new(Path.Combine(_folder, "stash.json"),
        new EphemeralDataProtectionProvider().CreateProtector("test"));
    private JsonHydrusInstanceStore CreateHydrusStore() => new(Path.Combine(_folder, "hydrus.json"),
        new EphemeralDataProtectionProvider().CreateProtector("test"));
    public void Dispose() => Directory.Delete(_folder, true);
}