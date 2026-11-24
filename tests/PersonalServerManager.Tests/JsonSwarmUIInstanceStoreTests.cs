using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class JsonSwarmUIInstanceStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("psm-swarmui-").FullName;
    private string FilePath => Path.Combine(_folder, "swarmui-instances.json");

    [Theory]
    [InlineData("http://192.168.1.25:5289/backends", "http://192.168.1.25:7801/")]
    [InlineData("https://server.example:5289/backends", "http://server.example:7801/")]
    [InlineData("http://localhost:5289/backends", "http://localhost:7801/")]
    public void ManagedNetworkBrowserUrlUsesManagerHost(string managerUrl, string expected)
    {
        var instance = new SwarmUIInstance(Guid.NewGuid(), "Managed", new Uri("http://127.0.0.1:7801/"),
            SwarmUIInstance.DefaultSearchPaths, _folder, ListenOnLocalNetwork: true);
        Assert.Equal(expected, instance.BrowserUrl(new Uri(managerUrl)).AbsoluteUri);
        Assert.Equal(instance.BaseUrl, (instance with { ListenOnLocalNetwork = false }).BrowserUrl(new Uri(managerUrl)));
        Assert.Equal(instance.BaseUrl, (instance with { InstallationFolder = null }).BrowserUrl(new Uri(managerUrl)));
    }

    [Fact]
    public async Task RoundTripsMultipleSearchPathsAndOrdersByName()
    {
        var store = new JsonSwarmUIInstanceStore(FilePath);
        var second = CreateInstance("Zeta") with { SearchPaths = [new("raw", 2), new("Starred/raw", 10), new(" \\custom\\sub/ ", 1)] };
        await store.SaveAsync(second);
        await store.SaveAsync(CreateInstance("Alpha"));

        var loaded = await store.GetAllAsync();
        Assert.Equal(["Alpha", "Zeta"], loaded.Select(item => item.Name));
        Assert.Equal([new SwarmUISearchPath("raw", 2), new("Starred/raw", 10), new("custom/sub", 1)], loaded[1].SearchPaths);
        Assert.Equal(second.BaseUrl, loaded[1].BaseUrl);
        Assert.Equal(second.Id, loaded[1].Id);
        Assert.False(loaded[1].IsManaged);
    }

    [Fact]
    public async Task MissingSearchPathsDefaultToRawWithDepthThree()
    {
        await File.WriteAllTextAsync(FilePath,
            "[{\"id\":\"b5f6e5f1-b07c-49cc-a9a2-236713903511\",\"name\":\"Legacy\",\"baseUrl\":\"http://localhost:7801/\"}]");
        var instance = Assert.Single(await new JsonSwarmUIInstanceStore(FilePath).GetAllAsync());
        Assert.Equal([new SwarmUISearchPath("raw", 3)], instance.SearchPaths);
        Assert.Equal(SwarmUIInstance.DefaultPort, instance.Port);
        Assert.False(instance.StartWithApp);
        Assert.False(instance.ListenOnLocalNetwork);
    }

    [Fact]
    public async Task ManagedNetworkSettingRoundTripsAndIsClearedForExternalInstances()
    {
        var binaries = Directory.CreateDirectory(Path.Combine(_folder, "src", "bin", "live_release"));
        await File.WriteAllTextAsync(Path.Combine(binaries.FullName, "SwarmUI.exe"), string.Empty);
        var store = new JsonSwarmUIInstanceStore(FilePath);
        var instance = new SwarmUIInstance(Guid.NewGuid(), "Managed", new Uri("http://127.0.0.1:7801/"),
            SwarmUIInstance.DefaultSearchPaths, _folder, ListenOnLocalNetwork: true);
        await store.SaveAsync(instance);
        Assert.True(Assert.Single(await store.GetAllAsync()).ListenOnLocalNetwork);
        await store.UpdateAsync(instance with { ListenOnLocalNetwork = false });
        Assert.False(Assert.Single(await store.GetAllAsync()).ListenOnLocalNetwork);
        await store.UpdateAsync(instance with { InstallationFolder = null, ListenOnLocalNetwork = true });
        Assert.False(Assert.Single(await store.GetAllAsync()).ListenOnLocalNetwork);
    }

    [Theory]
    [InlineData("../x", 3)]
    [InlineData("C:\\x", 3)]
    [InlineData("a//b", 3)]
    [InlineData("", 3)]
    [InlineData("raw", 0)]
    [InlineData("raw", 11)]
    public async Task InvalidSearchPathsAreRejected(string path, int depth)
    {
        var store = new JsonSwarmUIInstanceStore(FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(CreateInstance("Swarm") with { SearchPaths = [new(path, depth)] }));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task DuplicateEmptyAndTooManySearchPathsAreRejected()
    {
        var store = new JsonSwarmUIInstanceStore(FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(CreateInstance("Swarm") with { SearchPaths = [new("raw", 2), new("/raw/", 3)] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance("Swarm") with { SearchPaths = [] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance("Swarm") with
        {
            SearchPaths = Enumerable.Range(0, SwarmUIInstance.MaximumSearchPaths + 1).Select(index => new SwarmUISearchPath($"p{index}")).ToArray()
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance(new string('n', 61))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance("Swarm") with { BaseUrl = new Uri("ftp://localhost/") }));
    }

    [Fact]
    public async Task InvalidStoredPathsAndCorruptFilesAreLeftUntouched()
    {
        var store = new JsonSwarmUIInstanceStore(FilePath);
        foreach (var content in new[]
        {
            "{invalid json",
            "[{\"id\":\"b5f6e5f1-b07c-49cc-a9a2-236713903511\",\"name\":\"Bad\",\"baseUrl\":\"http://localhost:7801/\",\"searchPaths\":[{\"path\":\"../x\",\"depth\":3}]}]"
        })
        {
            await File.WriteAllTextAsync(FilePath, content);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance("Swarm")));
            Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
        }
    }

    [Fact]
    public async Task RemoveDeletesOnlyTheMatchingInstance()
    {
        var store = new JsonSwarmUIInstanceStore(FilePath);
        var first = CreateInstance("First");
        var second = CreateInstance("Second");
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.RemoveAsync(first.Id);
        await store.RemoveAsync(Guid.NewGuid());
        Assert.Equal(second.Id, Assert.Single(await store.GetAllAsync()).Id);
    }

    private static SwarmUIInstance CreateInstance(string name) =>
        new(Guid.NewGuid(), name, new Uri("http://localhost:7801/"), SwarmUIInstance.DefaultSearchPaths);

    public void Dispose() => Directory.Delete(_folder, true);
}
