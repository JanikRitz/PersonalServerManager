using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalServerManager.Hydrus;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class HydrusFeedQueryTests
{
    [Theory]
    [InlineData("system:width > 1000")]
    [InlineData("system:height <= 2000 pixels")]
    [InlineData("system:width != 50")]
    [InlineData("system:duration > 5 seconds")]
    [InlineData("system:duration <= 1 hour 2 minutes 3 seconds 4 ms")]
    [InlineData("system:duration ~= 500 milliseconds")]
    [InlineData("system:has duration")]
    [InlineData("system:no duration")]
    [InlineData("system:has tags")]
    public void Parse_PreservesSupportedApiPredicates(string expression)
    {
        Assert.Equal(expression, Assert.Single(HydrusFeedQuery.Parse(expression)));
    }

    [Theory]
    [InlineData("system:width > 100 AND system:width < 200", "[\"system:width > 100\",\"system:width < 200\"]")]
    [InlineData("system:height > 2 AND system:width > 3", "[\"system:height > 2\",\"system:width > 3\"]")]
    [InlineData("system:inbox AND (system:duration > 5 seconds OR blue eyes)", "[\"system:inbox\",[\"system:duration > 5 seconds\",\"blue eyes\"]]")]
    [InlineData("system:has audio OR system:no audio", "[[\"system:has audio\",\"system:no audio\"]]")]
    [InlineData("NOT system:has audio AND system:width > 1000", "[\"system:no audio\",\"system:width > 1000\"]")]
    public void Parse_CompilesMultiplePredicatesWithoutChangingMeaning(string expression, string expected)
    {
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonSerializer.SerializeToNode(HydrusFeedQuery.Parse(expression))));
    }

    [Theory]
    [InlineData("system:limit=4")]
    [InlineData("system:duration > 5")]
    [InlineData("system:duration > 5 min")]
    [InlineData("system:duration > 05 seconds")]
    [InlineData("system:duration > 1.5 seconds")]
    [InlineData("system:duration > 1 second 2 hours")]
    [InlineData("system:width > 5.0")]
    [InlineData("system:width > 1000000000")]
    [InlineData("NOT system:duration > 5 seconds")]
    [InlineData("system:has audio AND system:no audio")]
    [InlineData("system:inbox AND system:archive")]
    public void Parse_RejectsUnsupportedOrUnsafePredicates(string expression)
    {
        var error = Assert.Throws<NotSupportedException>(() => HydrusFeedQuery.Parse(expression));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Parse_RejectsExcessiveComplexity()
    {
        var expression = string.Join(" AND ", Enumerable.Repeat("tag", 129));
        Assert.Throws<NotSupportedException>(() => HydrusFeedQuery.Parse(expression));
    }

    [Theory]
    [InlineData('a')]
    [InlineData(' ')]
    public void Parse_RejectsOverlongQuery(char character)
    {
        Assert.Throws<ArgumentException>(() => HydrusFeedQuery.Parse(new string(character, 16385)));
    }
}