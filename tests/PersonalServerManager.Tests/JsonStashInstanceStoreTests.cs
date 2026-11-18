using Microsoft.AspNetCore.DataProtection;
using PersonalServerManager.Stash;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class JsonStashInstanceStoreTests
{
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