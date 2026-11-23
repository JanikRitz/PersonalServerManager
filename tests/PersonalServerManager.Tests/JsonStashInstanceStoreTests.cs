using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.Hydrus;
using PersonalServerManager.LocalLibraries;
using PersonalServerManager.Management;
using PersonalServerManager.Stash;
using PersonalServerManager.SwarmUI;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class JsonStashInstanceStoreTests
{
    [Fact]
    public async Task UpdateOnlyNeverRecreatesRemovedInstancesAcrossBackends()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
            var stash = new JsonStashInstanceStore(Path.Combine(directory, "stash.json"), protector);
            var stashInstance = CreateInstance("private-test-key");
            await stash.SaveAsync(stashInstance);
            await stash.UpdateAsync(stashInstance with { ApiKey = null });
            Assert.Equal("private-test-key", Assert.Single(await stash.GetAllAsync()).ApiKey);
            Assert.DoesNotContain("private-test-key", await File.ReadAllTextAsync(Path.Combine(directory, "stash.json")));
            await CheckUpdateRace(() => stash.SaveAsync(stashInstance), () => stash.UpdateAsync(stashInstance with { Name = "Updated", ApiKey = null }),
                () => stash.RemoveAsync(stashInstance.Id), async () => (await stash.GetAllAsync()).Count);

            var hydrus = new JsonHydrusInstanceStore(Path.Combine(directory, "hydrus.json"), protector);
            var hydrusInstance = new HydrusInstance(Guid.NewGuid(), "Hydrus", new Uri("http://localhost:45869/"), "private-test-key");
            await hydrus.SaveAsync(hydrusInstance);
            await hydrus.UpdateAsync(hydrusInstance with { ApiKey = null });
            Assert.Equal("private-test-key", Assert.Single(await hydrus.GetAllAsync()).ApiKey);
            Assert.DoesNotContain("private-test-key", await File.ReadAllTextAsync(Path.Combine(directory, "hydrus.json")));
            await CheckUpdateRace(() => hydrus.SaveAsync(hydrusInstance), () => hydrus.UpdateAsync(hydrusInstance with { Name = "Updated", ApiKey = null }),
                () => hydrus.RemoveAsync(hydrusInstance.Id), async () => (await hydrus.GetAllAsync()).Count);

            var swarm = new JsonSwarmUIInstanceStore(Path.Combine(directory, "swarm.json"));
            var swarmInstance = new SwarmUIInstance(Guid.NewGuid(), "Swarm", new Uri("http://localhost:7801/"), SwarmUIInstance.DefaultSearchPaths);
            await CheckUpdateRace(() => swarm.SaveAsync(swarmInstance), () => swarm.UpdateAsync(swarmInstance with { Name = "Updated" }),
                () => swarm.RemoveAsync(swarmInstance.Id), async () => (await swarm.GetAllAsync()).Count);

            var libraries = new JsonLocalLibraryStore(Path.Combine(directory, "libraries.json"));
            var library = new LocalLibraryInstance(Guid.NewGuid(), "Notes", directory, LocalLibraryKind.Notes);
            await CheckUpdateRace(() => libraries.SaveAsync(library), () => libraries.UpdateAsync(library with { Name = "Updated" }),
                () => libraries.RemoveAsync(library.Id), async () => (await libraries.GetAllAsync()).Count);

            using var comic = new ComicCompanionService(Path.Combine(directory, "comic.json"), NullLogger<ComicCompanionService>.Instance);
            var comicInstance = new ComicCompanionSettings(new Uri("http://localhost:5053/")) { Id = Guid.NewGuid(), Name = "Comics" };
            await CheckUpdateRace(async () => { await comic.SaveSettingsAsync(comicInstance); },
                async () => { await comic.UpdateSettingsAsync(comicInstance with { Name = "Updated" }); },
                () => comic.RemoveSettingsAsync(comicInstance.Id), async () => (await comic.GetAllAsync()).Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckUpdateRace(Func<Task> create, Func<Task> update, Func<Task> remove, Func<Task<int>> count)
    {
        await create();
        await update();
        await remove();
        await Assert.ThrowsAsync<InvalidDataException>(update);
        Assert.Equal(0, await count());

        for (var iteration = 0; iteration < 10; iteration++)
        {
            await create();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updating = Task.Run(async () =>
            {
                await start.Task;
                try { await update(); }
                catch (InvalidDataException) { }
            });
            var removing = Task.Run(async () => { await start.Task; await remove(); });
            start.SetResult();
            await Task.WhenAll(updating, removing);
            Assert.Equal(0, await count());
        }

        await create();
        Assert.Equal(1, await count());
        await remove();
    }

    [Fact]
    public async Task OptionalManagementFieldsRoundTripAndLegacyEndpointsRemainUnchanged()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "instances.json");
            var store = new JsonStashInstanceStore(path, new EphemeralDataProtectionProvider().CreateProtector("test"));
            await File.WriteAllTextAsync(Path.Combine(directory, "stash-win.exe"), string.Empty);
            var instance = CreateInstance(null) with { InstallationFolder = directory, Port = 9999, StartWithApp = true,
                GraphQlEndpoint = new Uri("http://127.0.0.1:9999/graphql"), WebUiUrl = new Uri("http://127.0.0.1:9999/") };
            instance.JobSettings.Scan.Paths = ["C:\\Media"];
            instance.JobSettings.Generate.Covers = true;
            await store.SaveAsync(instance);
            var saved = Assert.Single(await store.GetAllAsync());
            Assert.Equal(instance, saved with { JobSettings = instance.JobSettings });
            Assert.Equal(instance.JobSettings.Scan.Paths, saved.JobSettings.Scan.Paths);
            Assert.True(saved.JobSettings.Generate.Covers);
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new[] { new
            {
                instance.Id, instance.Name, GraphQlEndpoint = "https://example.test/custom/graphql",
                WebUiUrl = "https://example.test/custom/", ProtectedApiKey = (string?)null
            } }));
            var legacy = Assert.Single(await store.GetAllAsync());
            Assert.False(legacy.IsManaged);
            Assert.Equal(9999, legacy.Port);
            Assert.False(legacy.StartWithApp);
            await store.SaveAsync(legacy);
            var roundTrippedLegacy = Assert.Single(await store.GetAllAsync());
            Assert.Equal(legacy, roundTrippedLegacy with { JobSettings = legacy.JobSettings });
            Assert.False(roundTrippedLegacy.JobSettings.Generate.Covers);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SaveAsync_ProtectsApiKeyAndRetainsItWhenAnEditLeavesItBlank()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var filePath = Path.Combine(directory, "stash-instances.json");
            var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
            var store = new JsonStashInstanceStore(filePath, protector);
            var instance = CreateInstance("private-test-key");

            await store.SaveAsync(instance);
            var persisted = await File.ReadAllTextAsync(filePath);
            Assert.DoesNotContain("private-test-key", persisted);

            await store.SaveAsync(instance with { Name = "Renamed", ApiKey = null });
            var saved = Assert.Single(await store.GetAllAsync());
            Assert.Equal("Renamed", saved.Name);
            Assert.Equal("private-test-key", saved.ApiKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceInvalidConfiguration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var filePath = Path.Combine(directory, "stash-instances.json");
            const string invalidConfiguration = "{invalid json";
            await File.WriteAllTextAsync(filePath, invalidConfiguration);
            var store = new JsonStashInstanceStore(
                filePath,
                new EphemeralDataProtectionProvider().CreateProtector("test"));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(CreateInstance(null)));

            Assert.Equal(invalidConfiguration, await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static StashInstance CreateInstance(string? apiKey) => new(
        Guid.Parse("c7a6a4c0-c79f-4e3b-8aa9-8665d0564cbd"),
        "Primary Stash",
        new Uri("http://localhost:9999/graphql"),
        new Uri("http://localhost:9999/"),
        apiKey);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}