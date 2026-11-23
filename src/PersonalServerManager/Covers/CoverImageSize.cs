namespace PersonalServerManager.Covers;

public sealed record CoverAspectRatio(string Label, int Width, int Height, string? Name = null);

public static class CoverImageSize
{
    public static IReadOnlyList<int> Sizes { get; } = Array.AsReadOnly(Enumerable.Range(0, 13).Select(index => 512 + index * 128).ToArray());
    public static IReadOnlyList<CoverAspectRatio> Ratios { get; } = Array.AsReadOnly(new CoverAspectRatio[]
    {
        new("1:1", 1, 1, "Square"),
        new("4:3", 4, 3, "Old PC"),
        new("3:2", 3, 2, "Semi-Wide"),
        new("8:5", 8, 5),
        new("16:9", 16, 9, "Standard Widescreen"),
        new("21:9", 21, 9, "Ultra-Widescreen"),
        new("3:4", 3, 4),
        new("2:3", 2, 3, "Semi-tall"),
        new("9:16", 9, 16, "Tall"),
        new("9:21", 9, 21, "Ultra-tall")
    });

    public static (int Width, int Height) Calculate(int size, string ratio)
    {
        if (!Sizes.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size));
        var aspect = Ratios.FirstOrDefault(item => item.Label == ratio)
            ?? throw new ArgumentException("Invalid aspect ratio.", nameof(ratio));
        var shorterSide = (int)Math.Round((double)size * Math.Min(aspect.Width, aspect.Height) /
            Math.Max(aspect.Width, aspect.Height) / 64, MidpointRounding.AwayFromZero) * 64;
        return aspect.Width < aspect.Height ? (shorterSide, size) : (size, shorterSide);
    }
}