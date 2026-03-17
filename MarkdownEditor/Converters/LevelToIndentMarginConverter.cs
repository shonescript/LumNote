using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Converters;

/// <summary>将树节点（DataContext）的 Level 转为左侧缩进 Thickness，用于覆盖 TreeViewItem 错误缩进（Avalonia 空子节点多算一层）。</summary>
public sealed class LevelToIndentMarginConverter : IValueConverter
{
    public static readonly LevelToIndentMarginConverter Instance = new();

    /// <summary>每层缩进像素，与 Fluent 主题默认一致。</summary>
    private const double IndentPerLevel = 12;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = (value as FileTreeNode)?.Level ?? 0;
        if (level < 0) level = 0;
        var left = level * IndentPerLevel;
        return new Thickness(left, 0, 0, 0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
