using MarkdownEditor.Core;
using Xunit;

namespace MarkdownEditor.Tests.Core;

public class ColorUtilsTests
{
    [Theory]
    [InlineData(null, 0xFFD0D7DE)]
    [InlineData("", 0xFFD0D7DE)]
    [InlineData("   ", 0xFFD0D7DE)]
    [InlineData("#ffffff", 0xFFFFFFFF)]
    [InlineData("#000000", 0xFF000000)]
    [InlineData("#FF0000", 0xFFFF0000)]
    [InlineData("#00FF00", 0xFF00FF00)]
    [InlineData("#0000FF", 0xFF0000FF)]
    [InlineData("ffffff", 0xFFFFFFFF)]
    [InlineData("#D0D7DE", 0xFFD0D7DE)]
    public void ParseHexColor_RRGGBB_or_empty_returns_expected(string? hex, uint expected)
    {
        var result = ColorUtils.ParseHexColor(hex);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("#FFFFFFFF", 0xFFFFFFFF)]
    [InlineData("#80000000", 0x80000000)]
    [InlineData("#503399FF", 0x503399FF)]
    [InlineData("AABBCCDD", 0xAABBCCDD)]
    public void ParseHexColor_AARRGGBB_returns_expected(string hex, uint expected)
    {
        var result = ColorUtils.ParseHexColor(hex);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseHexColor_DefaultHexColor_constant_matches_used_default()
    {
        Assert.Equal(0xFFD0D7DEu, ColorUtils.DefaultHexColor);
        Assert.Equal(ColorUtils.DefaultHexColor, ColorUtils.ParseHexColor(""));
    }

    [Theory]
    [InlineData("#abc", 0xFFD0D7DE)]
    [InlineData("#12345", 0xFFD0D7DE)]
    public void ParseHexColor_invalid_length_returns_default(string hex, uint expected)
    {
        var result = ColorUtils.ParseHexColor(hex);
        Assert.Equal(expected, result);
    }
}
