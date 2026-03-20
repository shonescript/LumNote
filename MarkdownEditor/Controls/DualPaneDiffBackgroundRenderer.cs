using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using DiffPlex.DiffBuilder.Model;

namespace MarkdownEditor.Controls;

/// <summary>侧栏 diff 单行背景：左栏标删除/修改，右栏标插入/修改，对齐占位行用浅灰斜纹。</summary>
public sealed class DualPaneDiffBackgroundRenderer : IBackgroundRenderer
{
    private readonly Func<IReadOnlyList<ChangeType>?> _getLineTypes;
    private readonly bool _isOldPane;
    private readonly IBrush _emphasisBrush;
    private readonly IBrush _alignBrush;
    private readonly IBrush _hatchLineBrush;

    public DualPaneDiffBackgroundRenderer(
        Func<IReadOnlyList<ChangeType>?> getLineTypes,
        bool isOldPane,
        IBrush? emphasisBrush = null,
        IBrush? alignBrush = null)
    {
        _getLineTypes = getLineTypes;
        _isOldPane = isOldPane;
        _emphasisBrush = emphasisBrush ?? new SolidColorBrush(Color.FromArgb(0x50, 0xC0, 0x00, 0x00));
        _alignBrush = alignBrush ?? new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        _hatchLineBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x50, 0x50, 0x50));
    }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var kinds = _getLineTypes();
        if (kinds == null || kinds.Count == 0) return;
        var document = textView.Document;
        if (document == null) return;
        double fullWidth = 0;
        if (textView is Avalonia.Controls.Control ctrl)
            fullWidth = ctrl.Bounds.Width;
        if (fullWidth <= 0) return;

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine?.LineNumber ?? 0;
            if (lineNumber <= 0 || lineNumber > kinds.Count) continue;
            var t = kinds[lineNumber - 1];
            if (t == ChangeType.Imaginary)
            {
                DrawImaginaryLine(textView, drawingContext, line, fullWidth);
                continue;
            }
            var brush = PickBrush(t);
            if (brush == null) continue;
            var segs = BackgroundGeometryBuilder.GetRectsFromVisualSegment(textView, line, 0, line.VisualLength);
            foreach (var r in segs)
                drawingContext.DrawRectangle(brush, null, r);
            if (!segs.Any())
            {
                var y = line.VisualTop;
                var h = line.Height;
                drawingContext.DrawRectangle(brush, null, new Rect(0, y, fullWidth, h));
            }
        }
    }

    private void DrawImaginaryLine(TextView textView, DrawingContext drawingContext, VisualLine line, double fullWidth)
    {
        var segs = BackgroundGeometryBuilder.GetRectsFromVisualSegment(textView, line, 0, line.VisualLength);
        if (segs.Any())
        {
            foreach (var r in segs)
                DrawImaginaryRect(drawingContext, r);
        }
        else
        {
            var y = line.VisualTop;
            var h = line.Height;
            DrawImaginaryRect(drawingContext, new Rect(0, y, fullWidth, h));
        }
    }

    private void DrawImaginaryRect(DrawingContext dc, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        dc.DrawRectangle(_alignBrush, null, r);
        var pen = new Pen(_hatchLineBrush, 1);
        using (dc.PushClip(r))
        {
            const double step = 4;
            var left = r.Left;
            var top = r.Top;
            var w = r.Width;
            var h = r.Height;
            for (double i = -h; i < w + h; i += step)
                dc.DrawLine(pen, new Point(left + i, top), new Point(left + i + h, top + h));
        }
    }

    private IBrush? PickBrush(ChangeType t)
    {
        if (_isOldPane)
        {
            return t switch
            {
                ChangeType.Deleted or ChangeType.Modified => _emphasisBrush,
                _ => null
            };
        }
        return t switch
        {
            ChangeType.Inserted or ChangeType.Modified => _emphasisBrush,
            _ => null
        };
    }
}
