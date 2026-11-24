using PersonalServerManager.Covers;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class CoverPresetStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("psm-cover-presets-").FullName;
    private string FilePath => Path.Combine(_directory, "presets.json");

    [Fact]
    public async Task FirstReadCreatesDefaultPresetFromInstruction()
    {
        var store = new CoverPresetStore(FilePath);

        var preset = Assert.Single(await store.GetAllAsync("Custom instruction"));

        Assert.Equal("Default", preset.Name);
        Assert.Equal("Custom instruction", preset.MetaPrompt);
        Assert.True(File.Exists(FilePath));
        Assert.Equal(preset.Id, Assert.Single(await new CoverPresetStore(FilePath).GetAllAsync("Ignored")).Id);
    }

    [Fact]
    public async Task SaveUpdateAndRemoveRoundTrip()
    {
        var store = new CoverPresetStore(FilePath);
        var original = Assert.Single(await store.GetAllAsync("Instruction"));
        var swarm = Guid.NewGuid();
        var added = new CoverPreset(Guid.NewGuid(), " Comic ", swarm, " SDXL/model ", 30, 6.5, 1152, "2:3", "Meta",
            IncludeTextLines: true, TextLineCount: 60, IncludeFirstComicImage: true);

        await store.SaveAsync(added);
        await store.SaveAsync(original with { Steps = 8 });
        var presets = await new CoverPresetStore(FilePath).GetAllAsync("Instruction");

        Assert.Equal(new[] { "Comic", "Default" }, presets.Select(preset => preset.Name));
        var comic = presets[0];
        Assert.Equal(swarm, comic.SwarmInstanceId);
        Assert.Equal("SDXL/model", comic.Model);
        Assert.Equal((30, 6.5, 1152, "2:3", 60), (comic.Steps, comic.Cfg, comic.Size, comic.Ratio, comic.TextLineCount));
        Assert.True(comic.IncludeTextLines && comic.IncludeFirstComicImage);
        Assert.Equal(8, presets[1].Steps);

        await store.RemoveAsync(added.Id);
        Assert.Equal(original.Id, Assert.Single(await store.GetAllAsync("Instruction")).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(original.Id));
    }

    [Fact]
    public async Task RejectsInvalidPresetsAndInvalidFiles()
    {
        var store = new CoverPresetStore(FilePath);
        var valid = CoverPreset.CreateDefault("Meta");
        foreach (var invalid in new[]
        {
            valid with { Id = Guid.Empty }, valid with { Name = " " }, valid with { Name = new string('n', 61) },
            valid with { Model = "" }, valid with { Steps = 0 }, valid with { Cfg = double.NaN }, valid with { Cfg = 101 },
            valid with { Size = 1000 }, valid with { Ratio = "5:7" }, valid with { MetaPrompt = " " },
            valid with { MetaPrompt = new string('m', 8001) }, valid with { TextLineCount = 0 },
            valid with { TextLineCount = CoverPreset.MaximumTextLines + 1 }
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid));

        await File.WriteAllTextAsync(FilePath, "not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync("Meta"));
        Assert.Equal("not json", await File.ReadAllTextAsync(FilePath));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
