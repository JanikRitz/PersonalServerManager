using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LocalLibraryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-local-store-").FullName;

    [Fact]
    public async Task SavesUpdatesAndRemovesWithConcurrentSaves()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
        Assert.Empty(await store.GetAllAsync());
        var instances = Enumerable.Range(0, 10).Select(index =>
            new LocalLibraryInstance(Guid.NewGuid(), $"Library {index}", _root, LocalLibraryKind.Notes)).ToArray();
        await Task.WhenAll(instances.Select(instance => store.SaveAsync(instance)));
        Assert.Equal(10, (await store.GetAllAsync()).Count);
        await store.SaveAsync(instances[0] with { Name = "Updated" });
        Assert.Contains(await store.GetAllAsync(), instance => instance.Name == "Updated");
        await store.RemoveAsync(instances[0].Id);
        Assert.Equal(9, (await store.GetAllAsync()).Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":\"b5f6e5f1-b07c-49cc-a9a2-236713903511\",\"name\":\"Fixture\",\"rootPath\":\"relative\",\"kind\":0}]")]
    public async Task InvalidConfigurationIsPreserved(string invalid)
    {
        var path = Path.Combine(_root, "instances.json");
        await File.WriteAllTextAsync(path, invalid);
        var store = new JsonLocalLibraryStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            new LocalLibraryInstance(Guid.NewGuid(), "Notes", _root, LocalLibraryKind.Notes)));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync(Guid.NewGuid()));
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("\\relative")]
    [InlineData("C:\\bad\u0001path")]
    [InlineData("C:\\bad*path")]
    [InlineData("C:\\NUL")]
    [InlineData("C:\\AUX.txt")]
    [InlineData("C:\\COM1")]
    [InlineData("C:\\relative\\..\\escape")]
    public async Task RejectsUnsafeRoot(string root)
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            new LocalLibraryInstance(Guid.NewGuid(), "Notes", root, LocalLibraryKind.Notes)));
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAllAsync(cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_root, true);
}