using Microsoft.AspNetCore.DataProtection;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class JsonStashInstanceStoreTests
{
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
            await store.SaveAsync(instance);
            Assert.Equal(instance, Assert.Single(await store.GetAllAsync()));
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
            Assert.Equal(legacy, Assert.Single(await store.GetAllAsync()));
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