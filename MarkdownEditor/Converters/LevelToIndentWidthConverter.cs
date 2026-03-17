using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Converters;

/// <summary>将树节点 Level 转为缩进占位宽度（像素），用于文件树行首对齐。</summary>
public sealed class LevelToIndentWidthConverter : IValueConverter
{
    public static readonly LevelToIndentWidthConverter Instance = new();

    private const double IndentPerLevel = 12;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = (value as FileTreeNode)?.Level ?? 0;
        if (level < 0) level = 0;
        return level * IndentPerLevel;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
