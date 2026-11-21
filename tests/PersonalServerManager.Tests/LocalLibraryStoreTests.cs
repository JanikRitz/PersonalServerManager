using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class LocalLibraryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-local-store-").FullName;

    [Fact]
    public async Task ManagedCalibrePersistsSettingsAndDoesNotTakeOverAnOccupiedPort()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
        var settingsPath = Path.Combine(_root, "calibre-server.json");
        using var server = new ManagedCalibreServer(settingsPath, store, NullLogger<ManagedCalibreServer>.Instance);
        Assert.Equal(new ManagedCalibreSettings(), await server.GetSettingsAsync());
        await server.StartServerAsync();
        Assert.Contains("Add a Calibre library", server.GetStatus().Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => server.SaveSettingsAsync(new(65536, true)));

        var library = new LocalLibraryInstance(Guid.NewGuid(), "Comics", _root, LocalLibraryKind.CalibreComic);
        await store.SaveAsync(library);
        await File.WriteAllTextAsync(Path.Combine(_root, "metadata.db"), string.Empty);
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        try
        {
            var settings = new ManagedCalibreSettings(((IPEndPoint)listener.LocalEndpoint).Port, true);
            await server.SaveSettingsAsync(settings);
            using var restored = new ManagedCalibreServer(settingsPath, store, NullLogger<ManagedCalibreServer>.Instance);
            Assert.Equal(settings, await restored.GetSettingsAsync());
            await restored.StartServerAsync();
            Assert.Null(restored.GetStatus().Url);
            Assert.Contains("already in use", restored.GetStatus().Message);
            Assert.Equal(library, restored.WithReaderConnection(library));
            await restored.StopServerAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, settings.Port);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task PersistsCalibreReaderConfigurationAndBuildsNativeLinks()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "instances.json"));
        var instance = new LocalLibraryInstance(Guid.NewGuid(), "Comics", _root, LocalLibraryKind.CalibreComic,
            new Uri("https://calibre.example/reader"), "Books & Comics");
        await store.SaveAsync(instance);
        var restored = Assert.Single(await store.GetAllAsync());
        Assert.Equal(instance, restored);
        Assert.Equal("https://calibre.example/reader/#mode=read_book&library_id=Books%20%26%20Comics&book_id=42&fmt=cbz",
            restored.GetCalibreReaderUrl("42", "CBZ")?.AbsoluteUri);
        Assert.Equal("https://calibre.example/reader/get/pdf/42/Books%20%26%20Comics?content_disposition=inline",
            restored.GetCalibreReaderUrl("42", "PDF")?.AbsoluteUri);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { ContentServerLibraryId = null }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { Kind = LocalLibraryKind.Notes }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(instance with { ContentServerUrl = new Uri("https://secret@calibre.example/") }));
    }

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