using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Controls;

/// <summary>
/// 基于自研引擎的 Markdown 预览 - Skia 渲染，虚拟化，高性能
/// </summary>
public partial class MarkdownEngineView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<
        MarkdownEngineView,
        string?
    >(nameof(Markdown));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<MarkdownEngineView, MarkdownStyleConfig?>(nameof(StyleConfig));

    /// <summary>与 ViewModel.PreviewZoomLevel 绑定，变化时触发布局刷新。</summary>
    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<MarkdownEngineView, double>(nameof(ZoomLevel), 1.0);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownStyleConfig? StyleConfig
    {
        get => GetValue(StyleConfigProperty);
        set => SetValue(StyleConfigProperty, value);
    }

    private string? _lastMarkdown;
    private DispatcherTimer? _debounce;
    private DispatcherTimer? _scrollThrottle;
    private MarkdownEditor.Engine.Document.MutableStringDocumentSource? _documentSource;

    public MarkdownEngineView()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            try
            {
                _debounce.Stop();
                UpdateDocument();
            }
            catch
            {
                _debounce.Stop();
            }
        };

        MarkdownProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, e) =>
            {
                var view = c;
                view._debounce?.Stop();
                var newMd = view.Markdown ?? "";
                // 仅单字符变更（如 todo 勾选）时立即刷新预览，避免延迟与不同步
                if (
                    view._lastMarkdown != null
                    && newMd.Length == view._lastMarkdown.Length
                    && IsSingleCharChange(view._lastMarkdown, newMd)
                )
                {
                    view.UpdateDocument();
                    return;
                }
                view._debounce?.Start();
            }
        );

        StyleConfigProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, _) =>
            {
                if (c.RenderControl != null)
                    c.RenderControl.StyleConfig = c.StyleConfig;
            }
        );

        ZoomLevelProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, _) => c.RenderControl?.ResetEngine()
        );

        if (Scroll != null)
        {
            _scrollThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
            _scrollThrottle.Tick += (_, _) =>
            {
                try
                {
                    _scrollThrottle?.Stop();
                    RenderControl?.InvalidateVisual();
                }
                catch
                {
                    _scrollThrottle?.Stop();
                }
            };
            Scroll.ScrollChanged += (_, _) =>
            {
                if (RenderControl == null || Scroll == null)
                    return;
                RenderControl.ScrollOffset = (float)(Scroll.Offset.Y);
                _scrollThrottle?.Stop();
                _scrollThrottle?.Start();
            };
        }

        if (RenderControl != null)
            RenderControl.RequestScrollToY += (contentY) =>
            {
                if (Scroll != null)
                {
                    var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                    Scroll.Offset = new Avalonia.Vector(
                        Scroll.Offset.X,
                        Math.Clamp(contentY, 0, maxY)
                    );
                }
            };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (RenderControl != null)
            RenderControl.StyleConfig = StyleConfig;
        if (Markdown != _lastMarkdown)
            UpdateDocument();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RenderControl?.InvalidateVisual();
    }

    private void ClearSkipEditorToPreviewScrollSync()
    {
        if (DataContext is MainViewModel vm)
            vm.SkipEditorToPreviewScrollSync = false;
    }

    /// <summary>是否为单字符差异（如 todo [ ]↔[x]），用于跳过 debounce 立即刷新。</summary>
    private static bool IsSingleCharChange(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        int diff = 0;
        for (int i = 0; i < a.Length && diff <= 1; i++)
            if (a[i] != b[i])
                diff++;
        return diff == 1;
    }

    private void UpdateDocument()
    {
        var md = Markdown ?? "";
        if (md == _lastMarkdown)
            return;

        // 刷新前保存预览区滚动比例，布局完成后恢复，避免文档变更导致总高变化时滚动位置跳动
        double scrollRatio = 0;
        if (Scroll != null)
        {
            var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
            scrollRatio = maxY > 0 ? Math.Clamp(Scroll.Offset.Y / maxY, 0, 1) : 0;
        }

        _lastMarkdown = md;
        if (RenderControl == null)
            return;

        if (_documentSource == null)
            _documentSource = new MarkdownEditor.Engine.Document.MutableStringDocumentSource(md);
        else
            _documentSource.SetText(md);

        RenderControl.Document = _documentSource;
        RenderControl.InvalidateVisual();
        RenderControl.InvalidateMeasure();

        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    if (Scroll != null)
                    {
                        var newMaxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                        var newY = scrollRatio * newMaxY;
                        Scroll.Offset = new Avalonia.Vector(Scroll.Offset.X, newY);
                    }
                    ClearSkipEditorToPreviewScrollSync();
                }
                catch { }
            },
            DispatcherPriority.Loaded
        );
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        try
        {
            _debounce?.Stop();
            _scrollThrottle?.Stop();
        }
        catch { }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>将预览区当前选区复制到剪贴板。供窗口 Ctrl+C 在预览激活时调用。</summary>
    public System.Threading.Tasks.Task<bool> TryCopySelectionAsync() =>
        RenderControl?.TryCopySelectionToClipboardAsync() ?? System.Threading.Tasks.Task.FromResult(false);
}
