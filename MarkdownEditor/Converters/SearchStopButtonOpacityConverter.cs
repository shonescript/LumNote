using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MarkdownEditor.Converters;

/// <summary>搜索停止按钮透明度：搜索中为 1，否则半透明以表示不可用。</summary>
public sealed class SearchStopButtonOpacityConverter : IValueConverter
{
    public static readonly SearchStopButtonOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.45;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
