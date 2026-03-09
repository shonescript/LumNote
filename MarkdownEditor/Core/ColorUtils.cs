using System.Globalization;

namespace MarkdownEditor.Core;

/// <summary>颜色解析工具，供引擎与配置共用，便于单测与 AOT/Trimming。</summary>
public static class ColorUtils
{
    /// <summary>默认回退色（浅灰），ARGB 0xFFD0D7DE。</summary>
    public const uint DefaultHexColor = 0xFFD0D7DE;

    /// <summary>
    /// 将 #RRGGBB 或 #AARRGGBB 解析为 ARGB uint。
    /// 空或无效时返回 <see cref="DefaultHexColor"/>。
    /// </summary>
    public static uint ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return DefaultHexColor;
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return 0xFF000000u | uint.Parse(hex, NumberStyles.HexNumber);
        if (hex.Length == 8)
            return uint.Parse(hex, NumberStyles.HexNumber);
        return DefaultHexColor;
    }
}
