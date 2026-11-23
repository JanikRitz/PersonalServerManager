using PersonalServerManager.Covers;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class CoverImageSizeTests
{
    [Theory]
    [InlineData(1024, "3:4", 768, 1024)]
    [InlineData(1024, "4:3", 1024, 768)]
    [InlineData(512, "9:16", 320, 512)]
    [InlineData(2048, "16:9", 2048, 1152)]
    [InlineData(1024, "1:1", 1024, 1024)]
    [InlineData(1024, "8:5", 1024, 640)]
    [InlineData(1024, "21:9", 1024, 448)]
    [InlineData(1024, "9:21", 448, 1024)]
    public void Calculate_AlignsDimensionsTo64Pixels(int size, string ratio, int width, int height)
    {
        Assert.Equal((width, height), CoverImageSize.Calculate(size, ratio));
    }

    [Fact]
    public void Calculate_RejectsUnsupportedSelections()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverImageSize.Calculate(513, "3:4"));
        Assert.Throws<ArgumentException>(() => CoverImageSize.Calculate(1024, "unknown"));
    }
}