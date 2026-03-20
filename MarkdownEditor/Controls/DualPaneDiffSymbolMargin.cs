using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using DiffPlex.DiffBuilder.Model;

namespace MarkdownEditor.Controls;

/// <summary>
/// 双栏比对时在行号左侧绘制 + / -（左栏：删、改；右栏：增、改；对齐占位行不绘符号）。
/// </summary>
public sealed class DualPaneDiffSymbolMargin : Control
{
    private readonly Func<IReadOnlyList<ChangeType>?> _getLineTypes;
    private readonly bool _isOldPane;
    private readonly IBrush _minusBrush;
    private readonly IBrush _plusBrush;
    private TextView? _textView;

    private const double MarginWidth = 16;
    private const double FontSize = 12;

    public DualPaneDiffSymbolMargin(
        Func<IReadOnlyList<ChangeType>?> getLineTypes,
        bool isOldPane,
        IBrush? minusBrush = null,
        IBrush? plusBrush = null)
    {
        _getLineTypes = getLineTypes ?? throw new ArgumentNullException(nameof(getLineTypes));
        _isOldPane = isOldPane;
        _minusBrush = minusBrush ?? new SolidColorBrush(Color.FromRgb(0xC0, 0x40, 0x40));
        _plusBrush = plusBrush ?? new SolidColorBrush(Color.FromRgb(0x40, 0xC0, 0x40));
        Width = MarginWidth;
        MinWidth = MarginWidth;
        ClipToBounds = true;
    }

    public void AddToTextView(TextView textView)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        _textView.VisualLinesChanged += OnVisualLinesChanged;
    }

    public void RemoveFromTextView(TextView textView)
    {
        if (_textView != null)
        {
            _textView.VisualLinesChanged -= OnVisualLinesChanged;
            _textView = null;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var kinds = _getLineTypes();
        var textView = _textView;
        if (kinds == null || kinds.Count == 0 || textView == null) return;
        if (!textView.VisualLinesValid) return;
        var visualLines = textView.VisualLines;
        if (visualLines == null || visualLines.Count == 0) return;

        double scrollY = 0;
        try
        {
            if (visualLines.Count > 0)
                scrollY = visualLines[0].VisualTop;
        }
        catch (VisualLinesInvalidException)
        {
            return;
        }

        var typeface = new Typeface("Consolas");

        foreach (var line in visualLines)
        {
            var lineNumber = line.FirstDocumentLine?.LineNumber ?? 0;
            if (lineNumber <= 0 || lineNumber > kinds.Count) continue;
            var t = kinds[lineNumber - 1];
            var symbol = PickSymbol(t);
            if (symbol == null) continue;
            var brush = symbol == "-" ? _minusBrush : _plusBrush;
            var y = line.VisualTop - scrollY;
            var formatted = new FormattedText(
                symbol,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                brush);
            context.DrawText(formatted, new Point(2, y));
        }
    }

    private string? PickSymbol(ChangeType t)
    {
        if (_isOldPane)
        {
            return t switch
            {
                ChangeType.Deleted or ChangeType.Modified => "-",
                _ => null
            };
        }
        return t switch
        {
            ChangeType.Inserted or ChangeType.Modified => "+",
            _ => null
        };
    }
}
