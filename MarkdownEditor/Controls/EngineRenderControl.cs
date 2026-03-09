using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using MarkdownEditor.ViewModels;
using SkiaSharp;

namespace MarkdownEditor.Controls;

/// <summary>
/// Skia 渲染控件 - 作为 ScrollViewer 的内容，支持文本选择
/// </summary>
public class EngineRenderControl : Control
{
    public static readonly StyledProperty<IDocumentSource?> DocumentProperty =
        AvaloniaProperty.Register<EngineRenderControl, IDocumentSource?>(nameof(Document));

    public static readonly StyledProperty<float> ScrollOffsetProperty = AvaloniaProperty.Register<
        EngineRenderControl,
        float
    >(nameof(ScrollOffset));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<EngineRenderControl, MarkdownStyleConfig?>(nameof(StyleConfig));

    public IDocumentSource? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public float ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public MarkdownStyleConfig? StyleConfig
    {
        get => GetValue(StyleConfigProperty);
        set => SetValue(StyleConfigProperty, value);
    }

    private RenderEngine? _engine;
    private SelectionRange? _selection;
    private (int block, int offset)? _anchor;
    private DateTime _lastSelectionInvalidate = DateTime.MinValue;
    private bool _selectionInvalidateScheduled;

    /// <summary>上次测量时使用的有效宽度，避免 Document 变更触发的测量收到 0/NaN 导致宽度闪动。</summary>
    private double _lastValidMeasureWidth = 400;

    private EngineConfig? _cachedEffectiveConfig;
    private MarkdownStyleConfig? _cachedConfigStyleRef;
    private double _cachedConfigZoom = double.NaN;

    /// <summary>当前生效的引擎配置（已应用 ZoomLevel），用于边距与布局（与 RenderEngine 一致）。缓存以避免每帧分配。</summary>
    private EngineConfig EffectiveConfig
    {
        get
        {
            var style = StyleConfig;
            var zoom = style?.ZoomLevel ?? 1.0;
            if (_cachedEffectiveConfig != null && ReferenceEquals(_cachedConfigStyleRef, style) && Math.Abs(_cachedConfigZoom - zoom) < 1e-6)
                return _cachedEffectiveConfig;
            _cachedConfigStyleRef = style;
            _cachedConfigZoom = zoom;
            _cachedEffectiveConfig = (EngineConfig.FromStyle(style) ?? new EngineConfig()).WithZoomApplied();
            return _cachedEffectiveConfig;
        }
    }

    /// <summary>请求滚动到内容坐标 contentY，用于脚注上标/↩︎ 跳转。</summary>
    public event Action<float>? RequestScrollToY;

    public EngineRenderControl()
    {
        ClipToBounds = true;
        Focusable = true;
        StyleConfigProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) =>
            {
                c._engine = null;
                c._cachedEffectiveConfig = null;
                c._cachedConfigStyleRef = null;
            }
        );
        DocumentProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) =>
            {
                c._selection = null;
                c._anchor = null;
            }
        );
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnPointerEntered, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, OnPointerExited, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>清除引擎缓存，下次渲染时用当前 StyleConfig（含 ZoomLevel）重建。用于缩放等配置变更后刷新。</summary>
    public void ResetEngine()
    {
        _engine = null;
        _cachedEffectiveConfig = null;
        _cachedConfigStyleRef = null;
        InvalidateVisual();
    }

    private RenderEngine? GetOrCreateEngine()
    {
        if (_engine != null)
            return _engine;
        var config = EffectiveConfig;
        var w = (float)Math.Max(1, Bounds.Width - config.ContentPaddingX * 2);
        _engine = new RenderEngine(w, config);
        if (_engine.GetImageLoader() is DefaultImageLoader loader)
            loader.ImageLoaded += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { InvalidateVisual(); }
                    catch { }
                });
        return _engine;
    }

    /// <summary>与渲染时一致的滚动值（裁剪到有效范围），用于命中测试与光标对齐。</summary>
    private float GetClampedScrollY()
    {
        var doc = Document;
        if (doc == null)
            return 0;
        var engine = GetOrCreateEngine();
        var totalHeight = engine.MeasureTotalHeight(doc);
        var viewportH = (float)Math.Max(0, Bounds.Height);
        var maxScroll = Math.Max(0, totalHeight - viewportH);
        return Math.Clamp(ScrollOffset, 0, maxScroll);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Document != null)
        {
            var pt = e.GetPosition(this);
            var scrollY = GetClampedScrollY();
            var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
            var engine = GetOrCreateEngine();
            var hit = engine?.HitTest(Document, contentX, contentY);
            if (hit is { } h)
            {
                if (!string.IsNullOrEmpty(h.linkUrl))
                {
                    var url = h.linkUrl;
                    if (url.StartsWith("footnote:", StringComparison.Ordinal))
                    {
                        var y = engine?.GetContentYForFootnoteSection(Document);
                        if (y.HasValue)
                        {
                            RequestScrollToY?.Invoke(Math.Max(0, y.Value - 20));
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (url.StartsWith("footnote-back:", StringComparison.Ordinal))
                    {
                        var rest = url.AsSpan()["footnote-back:".Length..];
                        // 新格式：footnote-back:<id>（通过 id 在已布局 runs 中查找第一次出现的脚注引用位置）
                        // 旧格式：footnote-back:<blockIndex>:<charOffset>（兼容已有链接）
                        int colon = rest.IndexOf(':');
                        if (colon < 0)
                        {
                            var id = rest.ToString();
                            var y = engine?.GetContentYForFirstFootnoteRefId(Document, id);
                            if (y.HasValue)
                            {
                                RequestScrollToY?.Invoke(Math.Max(0, y.Value - 40));
                                e.Handled = true;
                                return;
                            }
                        }
                        else if (
                            int.TryParse(rest[..colon].ToString(), out int bi)
                            && int.TryParse(rest[(colon + 1)..].ToString(), out int co)
                        )
                        {
                            var y = engine?.GetContentYForBlockOffset(Document, bi, co);
                            if (y.HasValue)
                            {
                                RequestScrollToY?.Invoke(Math.Max(0, y.Value - 40));
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                    else if (url.StartsWith("todo-toggle:", StringComparison.Ordinal))
                    {
                        if (DataContext is MainViewModel vm && Document != null)
                        {
                            int blockIndex = h.blockIndex;
                            int lineIndexInBlock = h.lineIndexInBlock;
                            var lineOpt = engine?.GetTodoSourceLineForListBlock(
                                Document,
                                blockIndex,
                                lineIndexInBlock
                            );
                            if (lineOpt.HasValue)
                            {
                                var text = vm.CurrentMarkdown ?? string.Empty;
                                var idx = FindTodoMarkerOnLineByIndex(text, lineOpt.Value);
                                if (
                                    idx >= 0
                                    && idx + 2 < text.Length
                                    && text[idx] == '['
                                    && text[idx + 2] == ']'
                                )
                                {
                                    var chars = text.ToCharArray();
                                    var c = chars[idx + 1];
                                    chars[idx + 1] = (c == 'x' || c == 'X') ? ' ' : 'x';
                                    // 只修改编辑区内容，刷新由编辑区变更统一触发，渲染区不单独触发
                                    vm.CurrentMarkdown = new string(chars);
                                    e.Handled = true;
                                    return;
                                }
                            }
                        }
                    }
                    else
                    {
                        Core.OpenUrlService.Open(url);
                    }
                    e.Handled = true;
                    return;
                }
                _anchor = (h.blockIndex, h.charOffset);
                _selection = new SelectionRange(
                    h.blockIndex,
                    h.charOffset,
                    h.blockIndex,
                    h.charOffset
                );
                e.Handled = true;
                Focus();
                InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// 在给定文本中，定位第 lineIndex 行（0-based）上的 ToDo 标记 "[ ]" 或 "[x]" 的全局起始位置。
    /// 若该行无 ToDo 或行号越界则返回 -1。避免 Substring 以减 GC。
    /// </summary>
    private static int FindTodoMarkerOnLineByIndex(string text, int lineIndex)
    {
        if (string.IsNullOrEmpty(text) || lineIndex < 0)
            return -1;
        int lineStart = 0;
        for (int i = 0; i < lineIndex; i++)
        {
            int next = text.IndexOf('\n', lineStart);
            if (next < 0)
                return -1;
            lineStart = next + 1;
        }
        int lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd <= lineStart + 2)
            return -1;

        for (int i = lineStart; i + 3 <= lineEnd; i++)
        {
            if (text[i] == '[' && text[i + 2] == ']')
            {
                char c = text[i + 1];
                if (c == ' ' || c == 'x' || c == 'X')
                    return i;
            }
        }
        return -1;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        var scrollY = GetClampedScrollY();
        var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
        var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
        var engine = GetOrCreateEngine();

        if (
            _anchor is { } a
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Document != null
        )
        {
            var hit = engine?.HitTest(Document, contentX, contentY);
            if (hit is { } h)
            {
                int startBlock,
                    startOff,
                    endBlock,
                    endOff;
                if (a.block < h.blockIndex || (a.block == h.blockIndex && a.offset <= h.charOffset))
                {
                    startBlock = a.block;
                    startOff = a.offset;
                    endBlock = h.blockIndex;
                    endOff = h.charOffset;
                }
                else
                {
                    startBlock = h.blockIndex;
                    startOff = h.charOffset;
                    endBlock = a.block;
                    endOff = a.offset;
                }
                _selection = new SelectionRange(startBlock, startOff, endBlock, endOff);
                e.Handled = true;
                var now = DateTime.UtcNow;
                if ((now - _lastSelectionInvalidate).TotalMilliseconds >= 32)
                {
                    _lastSelectionInvalidate = now;
                    InvalidateVisual();
                }
                else if (!_selectionInvalidateScheduled)
                {
                    _selectionInvalidateScheduled = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () =>
                        {
                            try
                            {
                                _selectionInvalidateScheduled = false;
                                _lastSelectionInvalidate = DateTime.UtcNow;
                                InvalidateVisual();
                            }
                            catch
                            {
                                _selectionInvalidateScheduled = false;
                            }
                        },
                        Avalonia.Threading.DispatcherPriority.Background
                    );
                }
            }
        }
        else
        {
            UpdateCursor(engine, contentX, contentY);
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (Document != null)
        {
            var pt = e.GetPosition(this);
            var scrollY = GetClampedScrollY();
            var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
            UpdateCursor(GetOrCreateEngine(), contentX, contentY);
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        Cursor = Avalonia.Input.Cursor.Default;
        ToolTip.SetTip(this, null);
    }

    private void UpdateCursor(RenderEngine? engine, float contentX, float contentY)
    {
        if (engine == null || Document == null)
            return;
        var hit = engine.HitTest(Document, contentX, contentY);
        if (hit is { } h)
        {
            Cursor = !string.IsNullOrEmpty(h.linkUrl)
                ? new Avalonia.Input.Cursor(StandardCursorType.Hand)
                : h.isSelectable
                    ? new Avalonia.Input.Cursor(StandardCursorType.Ibeam)
                    : Avalonia.Input.Cursor.Default;
            bool isTodo =
                h.linkUrl != null && h.linkUrl.StartsWith("todo-toggle:", StringComparison.Ordinal);
            ToolTip.SetTip(
                this,
                isTodo ? null : (string.IsNullOrEmpty(h.linkUrl) ? null : h.linkUrl)
            );
        }
        else
        {
            Cursor = Avalonia.Input.Cursor.Default;
            ToolTip.SetTip(this, null);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (
            e.GetCurrentPoint(this).Properties.PointerUpdateKind
            is PointerUpdateKind.LeftButtonReleased
        )
        {
            _anchor = null;
            InvalidateVisual();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.C
            && (e.KeyModifiers & KeyModifiers.Control) != 0
            && TryCopySelectionToClipboardAsync().GetAwaiter().GetResult()
        )
        {
            e.Handled = true;
        }
    }

    /// <summary>将当前选区文本写入剪贴板。供本控件 KeyDown 与窗口 Ctrl+C 统一调用。有选区且写入成功返回 true。</summary>
    public async System.Threading.Tasks.Task<bool> TryCopySelectionToClipboardAsync()
    {
        if (_selection is not { } sel || sel.IsEmpty || Document == null)
            return false;
        var text = GetOrCreateEngine()?.GetSelectedText(Document, sel);
        if (string.IsNullOrEmpty(text))
            return false;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // 预览区域尺寸变化时，强制重置引擎与测量，
        // 避免旧宽度下的布局缓存导致文档底部未重新布局而出现空白。
        if (double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
            _lastValidMeasureWidth = e.NewSize.Width;

        ResetEngine();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var doc = Document;
        if (doc == null)
            return ClampMeasureSize(availableSize.Width, 100);

        // 一次性算准宽度：无效或零时用当前 Bounds 或上次有效值，避免变窄后复原的闪烁
        double availW =
            double.IsFinite(availableSize.Width) && availableSize.Width > 0
                ? availableSize.Width
                : (Bounds.Width > 0 ? Bounds.Width : _lastValidMeasureWidth);
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
            _lastValidMeasureWidth = availableSize.Width;

        var engine = GetOrCreateEngine();
        var w = (float)Math.Max(1, availW - EffectiveConfig.ContentPaddingX * 2);
        engine.SetWidth(w);
        float h = engine.MeasureTotalHeight(doc);
        float contentWidth = engine.MeasureContentWidth(doc);
        float totalWidth = EffectiveConfig.ContentPaddingX * 2 + contentWidth;
        double width = Math.Max(availW, (double)totalWidth);
        double height = Math.Max(100, (double)h);
        return ClampMeasureSize(width, height);
    }

    /// <summary>确保返回的尺寸为有限正数，避免 Avalonia 报 "Invalid size returned for Measure"。</summary>
    private static Size ClampMeasureSize(double width, double height)
    {
        if (!double.IsFinite(width) || width < 0)
            width = 400;
        if (!double.IsFinite(height) || height < 0)
            height = 100;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var doc = Document;
        if (doc == null)
            return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var rawScrollY = ScrollOffset;
        var config = EffectiveConfig;
        var w = (float)Math.Max(1, bounds.Width - config.ContentPaddingX * 2);

        if (_engine == null)
            _engine = new RenderEngine(w, config);
        else
            _engine.SetWidth(w);

        var viewportHeight = (float)bounds.Height;
        var totalHeight = _engine.MeasureTotalHeight(doc);

        // 将滚动偏移裁剪在 [0, max(0, totalHeight - viewportHeight)] 范围内，
        // 避免当 ScrollViewer 允许滚动超过内容高度时，内容整体被平移出视口只剩空白。
        var maxScroll = Math.Max(0, totalHeight - viewportHeight);
        var scrollY = Math.Clamp(rawScrollY, 0, maxScroll);

        var padX = EffectiveConfig.ContentPaddingX;
        var padY = EffectiveConfig.ContentPaddingY;
        context.Custom(
            new EngineDrawOp(
                new Rect(0, 0, bounds.Width, bounds.Height),
                doc,
                _engine,
                scrollY,
                w,
                viewportHeight,
                _selection,
                padX,
                padY
            )
        );
    }

    private sealed class EngineDrawOp : ICustomDrawOperation
    {
        private readonly Rect _rect;
        private readonly IDocumentSource _doc;
        private readonly RenderEngine _engine;
        private readonly float _scrollY;
        private readonly float _width;
        private readonly float _height;
        private readonly SelectionRange? _selection;
        private readonly float _contentPaddingX;
        private readonly float _contentPaddingY;

        public EngineDrawOp(
            Rect rect,
            IDocumentSource doc,
            RenderEngine engine,
            float scrollY,
            float width,
            float height,
            SelectionRange? selection,
            float contentPaddingX,
            float contentPaddingY
        )
        {
            _rect = rect;
            _doc = doc;
            _engine = engine;
            _scrollY = scrollY;
            _width = width;
            _height = height;
            _selection = selection;
            _contentPaddingX = contentPaddingX;
            _contentPaddingY = contentPaddingY;
        }

        public Rect Bounds => _rect;

        public bool HitTest(Point p) => true;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext ctx)
        {
            var lease = ctx.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null)
                return;

            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            if (canvas == null)
                return;

            // 直接绘制到主画布，不占用额外离屏缓冲；由 Avalonia 负责呈现与防撕裂
            RenderToCanvas(canvas);
        }

        private void RenderToCanvas(SKCanvas canvas)
        {
            var skCtx = new SkiaRenderContext
            {
                Canvas = canvas,
                Size = new SKSize(_width, _height),
                Scale = 1f
            };
            canvas.Save();
            canvas.Translate(_contentPaddingX, _contentPaddingY);
            _engine.Render(
                skCtx,
                _doc,
                _scrollY,
                _height,
                _selection.HasValue && !_selection.Value.IsEmpty ? _selection : null
            );
            canvas.Restore();
        }
    }
}
