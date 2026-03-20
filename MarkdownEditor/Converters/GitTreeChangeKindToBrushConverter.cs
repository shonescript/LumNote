using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MarkdownEditor.Models;

namespace MarkdownEditor.Converters;

/// <summary>树变更类型 → 前景色（删除偏红、新增偏绿）。</summary>
public sealed class GitTreeChangeKindToBrushConverter : IValueConverter
{
    public static readonly GitTreeChangeKindToBrushConverter Instance = new();

    private static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.FromRgb(204, 160, 0));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(114, 156, 82));
    private static readonly IBrush DeletedBrush = new SolidColorBrush(Color.FromRgb(189, 83, 74));
    private static readonly IBrush RenamedBrush = new SolidColorBrush(Color.FromRgb(120, 180, 200));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GitTreeChangeKind k)
            return DefaultBrush;
        return k switch
        {
            GitTreeChangeKind.Added => AddedBrush,
            GitTreeChangeKind.Modified => ModifiedBrush,
            GitTreeChangeKind.Deleted => DeletedBrush,
            GitTreeChangeKind.Renamed => RenamedBrush,
            GitTreeChangeKind.TypeChanged => ModifiedBrush,
            _ => DefaultBrush
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
