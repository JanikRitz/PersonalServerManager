using System.Text.Json;

namespace PersonalServerManager.Covers;

public sealed record CoverPreset(Guid Id, string Name, Guid? SwarmInstanceId, string Model, int Steps, double Cfg,
    int Size, string Ratio, string MetaPrompt, bool IncludeTextLines = false, int TextLineCount = 40,
    bool IncludeFirstComicImage = false)
{
    public const int MaximumTextLines = 2000;

    public static CoverPreset CreateDefault(string metaPrompt) =>
        new(Guid.NewGuid(), "Default", null, "ZImage/z_image_turbo_bf16", 12, 1, 1024, "3:4", metaPrompt);
}

public sealed class CoverPresetStore(string filePath)
{
    private const int MaximumPresets = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _filePath = Path.GetFullPath(filePath);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Returns all presets, creating a default preset from <paramref name="defaultMetaPrompt"/> on first use.</summary>
    public async Task<IReadOnlyList<CoverPreset>> GetAllAsync(string defaultMetaPrompt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var presets = await ReadAsync(cancellationToken);
            if (presets is null)
            {
                presets = [Validate(CoverPreset.CreateDefault(defaultMetaPrompt))];
                await WriteAsync(presets, cancellationToken);
            }
            return presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CoverPreset preset, CancellationToken cancellationToken = default)
    {
        preset = Validate(preset);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var presets = await ReadAsync(cancellationToken) ?? [];
            var index = presets.FindIndex(item => item.Id == preset.Id);
            if (index >= 0) presets[index] = preset;
            else if (presets.Count >= MaximumPresets) throw new InvalidDataException($"At most {MaximumPresets} presets can be saved.");
            else presets.Add(preset);
            await WriteAsync(presets, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var presets = await ReadAsync(cancellationToken) ?? [];
            if (presets.Count <= 1 && presets.Any(item => item.Id == id))
                throw new InvalidOperationException("The last preset cannot be deleted.");
            if (presets.RemoveAll(item => item.Id == id) > 0) await WriteAsync(presets, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public static CoverPreset Validate(CoverPreset? preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var name = preset.Name?.Trim();
        if (preset.Id == Guid.Empty) throw new InvalidDataException("The preset needs an ID.");
        if (string.IsNullOrEmpty(name) || name.Length > 60 || name.Any(char.IsControl))
            throw new InvalidDataException("Enter a preset name of up to 60 characters.");
        var model = preset.Model?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > 1024 || model.Any(char.IsControl))
            throw new InvalidDataException("Enter a SwarmUI model.");
        if (preset.Steps is < 1 or > 1000) throw new InvalidDataException("Steps must be between 1 and 1000.");
        if (!double.IsFinite(preset.Cfg) || preset.Cfg is < 0 or > 100) throw new InvalidDataException("CFG must be between 0 and 100.");
        if (!CoverImageSize.Sizes.Contains(preset.Size)) throw new InvalidDataException("Choose a supported image size.");
        if (preset.Ratio is null || CoverImageSize.Ratios.All(ratio => ratio.Label != preset.Ratio))
            throw new InvalidDataException("Choose a supported image format.");
        if (string.IsNullOrWhiteSpace(preset.MetaPrompt) || preset.MetaPrompt.Length > 8000 ||
            preset.MetaPrompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("The meta prompt must be between 1 and 8000 characters.");
        if (preset.TextLineCount is < 1 or > CoverPreset.MaximumTextLines)
            throw new InvalidDataException($"The line count must be between 1 and {CoverPreset.MaximumTextLines}.");
        return preset with { Name = name, Model = model };
    }

    private async Task<List<CoverPreset>?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
            var presets = await JsonSerializer.DeserializeAsync<List<CoverPreset>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException();
            if (presets.Count > MaximumPresets || presets.Select(item => item?.Id).Distinct().Count() != presets.Count)
                throw new InvalidDataException();
            return presets.Select(Validate).ToList();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException("The cover preset file is invalid. It was left untouched.");
        }
    }

    private async Task WriteAsync(List<CoverPreset> presets, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, presets, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _filePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
