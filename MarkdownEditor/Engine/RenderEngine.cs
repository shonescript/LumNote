using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Engine;

/// <summary>
/// 渲染引擎 - 统一入口
/// 文档 → 块扫描 → 解析（含缓存）→ 块级增量布局 → 渲染
/// </summary>
public sealed class RenderEngine
{
    /// <summary>块级缓存项：内容哈希用于变更检测，高度与可选完整布局用于局部更新。</summary>
    private sealed class BlockCacheEntry
    {
        public ulong ContentHash;
        public float CachedHeight = float.NaN;
        public LayoutBlock? CachedLayout;
    }

    /// <summary>FNV-1a 64 位哈希，用于块内容变更检测。</summary>
    private static ulong HashBlockContent(ReadOnlySpan<char> text)
    {
        const ulong FnvPrime = 1099511628211;
        const ulong FnvOffset = 14695981039346656037;
        ulong h = FnvOffset;
        foreach (char c in text)
        {
            h ^= c;
            h *= FnvPrime;
        }
        return h;
    }
    private readonly EngineConfig _config;
    private readonly ILayoutEngine _layout;
    private readonly SkiaRenderer _renderer;
    private readonly IImageLoader? _imageLoader;
    private float _width;

    /// <summary>当前文档完整文本的行起始索引（用于从块内偏移换算到全局字符偏移）。</summary>
    private int[]? _fullTextLineStarts;

    private IDocumentSource? _cachedDoc;
    private int _cachedDocVersion;
    private List<MarkdownNode?> _cachedBlocks = [];
    /// <summary>每个块对应的行范围 (StartLine, EndLine)，与 _cachedBlocks 一一对应。</summary>
    private List<(int startLine, int endLine)> _cachedBlockRanges = [];
    private List<LayoutBlock>? _cachedLayouts;
    private float _cachedTotalHeight;
    /// <summary>布局后内容的实际最大宽度（含代码块等长行），用于横向滚动。</summary>
    private float _cachedContentWidth;
    /// <summary>块级缓存：与 _cachedBlocks 一一对应，用于变更检测与局部布局。</summary>
    private List<BlockCacheEntry> _blockCache = [];
    /// <summary>累积 Y[i] = 块 0..i-1 的高度和，Y[0]=0，用于可见区间与 HitTest。</summary>
    private float[] _cumulativeY = [];
    /// <summary>当前增量布局窗口 [Start, End)，用于判断是否需重新构建 _cachedLayouts。</summary>
    private (int start, int end) _layoutWindow = (-1, -1);

    public RenderEngine(float width, EngineConfig? config = null, IImageLoader? imageLoader = null, ILayoutEngine? layout = null, SkiaRenderer? renderer = null)
    {
        _width = Math.Max(1, width);
        _config = config ?? new EngineConfig();
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _renderer = renderer ?? new SkiaRenderer(_config, _imageLoader);
        _layout = layout ?? new SkiaLayoutEngine(_config, _imageLoader, _renderer);
    }

    /// <summary>获取当前使用的图片加载器，可用于订阅 ImageLoaded 以在图片加载完成后重绘。</summary>
    public IImageLoader? GetImageLoader() => _imageLoader;

    public void SetWidth(float width)
    {
        var w = Math.Max(1, width);
        if (Math.Abs(w - _width) > 0.1f)
        {
            _width = w;
            _cachedLayouts = null;
            _layoutWindow = (-1, -1);
            for (int i = 0; i < _blockCache.Count; i++)
            {
                _blockCache[i].CachedHeight = float.NaN;
                _blockCache[i].CachedLayout = null;
            }
            _cumulativeY = [];
            _cachedTotalHeight = 0;
        }
    }

    /// <summary>
    /// 获取视口内需渲染的块区间（基于真实布局高度）。可多渲染一屏作为缓存。
    /// </summary>
    public (int startBlock, int endBlock) GetVisibleBlockRange(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return (0, 0);
        var blocks = _cachedLayouts;
        int start = FindFirstVisibleBlockIndex(blocks, scrollY);
        float limit = scrollY + viewportHeight * 2;
        int end = start;
        for (int i = start; i < blocks.Count; i++)
        {
            end = i + 1;
            if (blocks[i].Bounds.Top >= limit)
                break;
        }
        return (start, Math.Min(end, blocks.Count));
    }

    /// <summary>
    /// 获取视口内仅包含“整块”的区间（块底部不超过 scrollY+viewportHeight），用于 PDF 分页避免块被截断。
    /// </summary>
    public (int startBlock, int endBlock) GetVisibleBlockRangeFullBlocksOnly(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return (0, 0);
        var blocks = _cachedLayouts;
        int start = FindFirstVisibleBlockIndex(blocks, scrollY);
        float pageBottom = scrollY + viewportHeight;
        int end = start;
        for (int i = start; i < blocks.Count; i++)
        {
            if (blocks[i].Bounds.Top >= pageBottom)
                break;
            if (blocks[i].Bounds.Bottom <= pageBottom)
                end = i + 1;
        }
        return (start, end);
    }

    /// <summary>第一个满足 Bounds.Bottom >= scrollY 的块索引；若均在上方则返回 0。</summary>
    private static int FindFirstVisibleBlockIndex(List<LayoutBlock> blocks, float scrollY)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Bounds.Bottom >= scrollY)
                return i;
        }
        return 0;
    }

    /// <summary>
    /// 布局并渲染可见块
    /// </summary>
    public void Render(ISkiaRenderContext ctx, IDocumentSource doc, float scrollY, float viewportHeight,
        SelectionRange? selection = null)
    {
        Render(ctx, doc, scrollY, viewportHeight, selection, out _);
    }

    /// <summary>
    /// 布局并渲染可见块；若仅整块分页则传入 fullBlocksOnly=true 并得到 nextScrollY 用于下一页。
    /// </summary>
    public void Render(ISkiaRenderContext ctx, IDocumentSource doc, float scrollY, float viewportHeight,
        SelectionRange? selection, out float nextScrollY, bool fullBlocksOnly = false)
    {
        nextScrollY = scrollY + viewportHeight;
        if (_blockCache.Count == 0)
            return;
        // 按需布局：增量时只布局可见窗口，全量时在 HitTest/导出 等路径已做
        if (_layoutWindow.start >= 0 || _cachedLayouts == null)
            EnsureLayout(doc, scrollY, viewportHeight);
        var layouts = _cachedLayouts;
        if (layouts == null || layouts.Count == 0)
            return;

        var (startBlock, endBlock) = fullBlocksOnly
            ? GetVisibleBlockRangeFullBlocksOnly(doc, scrollY, viewportHeight)
            : GetVisibleBlockRange(doc, scrollY, viewportHeight);
        var count = Math.Max(0, endBlock - startBlock);
        startBlock = Math.Clamp(startBlock, 0, layouts.Count - 1);
        count = Math.Min(count, layouts.Count - startBlock);
        if (count > 0)
            nextScrollY = layouts[startBlock + count - 1].Bounds.Bottom;

        ctx.Canvas.Save();
        ctx.Canvas.Translate(0, -scrollY);
        _renderer.Render(ctx, layouts, startBlock, count, selection);
        ctx.Canvas.Restore();
    }

    private void ParseFullDocument(IDocumentSource doc)
    {
        var rawBlocks = new List<MarkdownNode?>();
        var rawRanges = new List<(int startLine, int endLine)>();
        int line = 0;
        while (line < doc.LineCount)
        {
            var span = BlockScanner.ScanNextBlock(doc, line);
            if (span.LineCount <= 0) break;
            var text = GetSpanText(doc, span);
            var fullDoc = MarkdownParser.Parse(text);
            foreach (var child in fullDoc.Children)
            {
                rawBlocks.Add(child);
                rawRanges.Add((span.StartLine, span.EndLine));
            }
            line = span.EndLine;
        }

        var (normalizedBlocks, normalizedRanges) = NormalizeFootnotes(rawBlocks, rawRanges);
        _cachedBlocks = normalizedBlocks;
        _cachedBlockRanges = normalizedRanges;
    }

    private static string GetSpanText(IDocumentSource doc, BlockSpan span)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (i > span.StartLine) sb.Append('\n');
            sb.Append(doc.GetLine(i).ToString());
        }
        return sb.ToString();
    }

    /// <summary>取块 i 的源码文本，用于内容哈希。</summary>
    private string GetBlockText(IDocumentSource doc, int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _cachedBlockRanges.Count)
            return string.Empty;
        var (startLine, endLine) = _cachedBlockRanges[blockIndex];
        var span = new BlockSpan(startLine, endLine, BlockKind.Paragraph);
        return GetSpanText(doc, span);
    }

    /// <summary>
    /// 命中测试 - 将文档坐标转为 (blockIndex, charOffset, isSelectable, linkUrl, lineIndexInBlock)
    /// lineIndexInBlock: 命中的布局行在该块内的索引（用于 todo 等按行定位）
    /// </summary>
    public (int blockIndex, int charOffset, bool isSelectable, string? linkUrl, int lineIndexInBlock)? HitTest(IDocumentSource doc, float contentX, float contentY)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null)
            return null;

        foreach (var layoutBlock in _cachedLayouts)
        {
            if (contentY < layoutBlock.Bounds.Top || contentY >= layoutBlock.Bounds.Bottom)
                continue;

            var localX = contentX - layoutBlock.Bounds.Left;

            for (int lineIdx = 0; lineIdx < layoutBlock.Lines.Count; lineIdx++)
            {
                var layoutLine = layoutBlock.Lines[lineIdx];
                var lineTop = layoutBlock.Bounds.Top + layoutLine.Y;
                var lineBottom = lineTop + layoutLine.Height;
                if (contentY < lineTop || contentY >= lineBottom)
                    continue;

                var runs = layoutLine.Runs;
                if (runs.Count == 0)
                    return (layoutBlock.BlockIndex, 0, false, null, lineIdx);

                LayoutRun? bestRun = null;
                float bestDist = float.MaxValue;
                int offsetInRun = 0;
                for (int i = 0; i < runs.Count; i++)
                {
                    var run = runs[i];
                    if (run.Text.Length == 0) continue;
                    if (localX >= run.Bounds.Left && localX < run.Bounds.Right)
                    {
                        float textLeft = run.Style is RunStyle.TableHeaderCell or RunStyle.TableCell
                            ? run.Bounds.Left + 8f
                            : run.Bounds.Left;
                        float xInRun = Math.Max(0, localX - textLeft);
                        offsetInRun = _layout.MeasureTextOffset(run.Text, xInRun, run.Style);
                        return (layoutBlock.BlockIndex, run.CharOffset + offsetInRun, true, run.LinkUrl, lineIdx);
                    }
                    var distLeft = run.Bounds.Left - localX;
                    var distRight = localX - run.Bounds.Right;
                    if (localX < run.Bounds.Left && distLeft < bestDist)
                    {
                        bestDist = Math.Abs(distLeft);
                        bestRun = run;
                        offsetInRun = 0;
                    }
                    else if (localX >= run.Bounds.Right && distRight < bestDist)
                    {
                        bestDist = Math.Abs(distRight);
                        bestRun = run;
                        offsetInRun = run.Text.Length;
                    }
                }
                if (bestRun != null)
                    return (layoutBlock.BlockIndex, bestRun.Value.CharOffset + offsetInRun, true, bestRun.Value.LinkUrl, lineIdx);
                return (layoutBlock.BlockIndex, runs[0].CharOffset, runs[0].Text.Length > 0, runs[0].LinkUrl, lineIdx);
            }
        }

        return null;
    }

    private void EnsureParsed(IDocumentSource doc)
    {
        int version = 0;
        if (doc is MarkdownEditor.Engine.Document.IVersionedDocumentSource vds)
            version = vds.Version;

        if (ReferenceEquals(doc, _cachedDoc) && version == _cachedDocVersion)
            return;

        var oldCache = _blockCache;
        _cachedDoc = doc;
        _cachedDocVersion = version;
        ParseFullDocument(doc);
        _cachedLayouts = null;
        _cachedTotalHeight = 0;
        _cachedContentWidth = 0;
        _layoutWindow = (-1, -1);
        _cumulativeY = [];

        // 切换文档后不得复用上一文档的 CachedLayout：其中的 LayoutRun.Text 来自旧 AST，会导致渲染区混入上一文档内容。
        // 仅复用 CachedHeight（同块文本时可减少高度跳动）。
        var newCache = new List<BlockCacheEntry>(_cachedBlocks.Count);
        for (int i = 0; i < _cachedBlocks.Count; i++)
        {
            string blockText = GetBlockText(doc, i);
            ulong newHash = HashBlockContent(blockText.AsSpan());
            if (i < oldCache.Count && oldCache[i].ContentHash == newHash)
            {
                newCache.Add(new BlockCacheEntry
                {
                    ContentHash = newHash,
                    CachedHeight = oldCache[i].CachedHeight,
                    CachedLayout = null
                });
            }
            else
            {
                newCache.Add(new BlockCacheEntry { ContentHash = newHash });
            }
        }
        _blockCache = newCache;
    }

    /// <summary>确保所有块均有缓存高度（缺失则布局取高），并更新 _cumulativeY 与 _cachedTotalHeight。</summary>
    private void EnsureBlockHeights(IDocumentSource doc)
    {
        if (_blockCache.Count == 0) return;

        float blockIndent = _config.BlockIndent;
        var contentWidth = Math.Max(1, _width - blockIndent);
        bool anyDirty = false;
        for (int i = 0; i < _blockCache.Count; i++)
        {
            if (float.IsNaN(_blockCache[i].CachedHeight))
            {
                var ast = _cachedBlocks[i];
                if (ast == null) continue;
                var (startLine, endLine) = i < _cachedBlockRanges.Count ? _cachedBlockRanges[i] : (0, 0);
                var layoutBlock = _layout.Layout(ast, contentWidth, i, startLine, endLine);
                _blockCache[i].CachedHeight = layoutBlock.Bounds.Height;
                anyDirty = true;
            }
        }
        if (anyDirty || _cumulativeY.Length != _blockCache.Count + 1)
            UpdateCumulativeY();
    }

    /// <summary>根据各块 CachedHeight 更新 _cumulativeY 与 _cachedTotalHeight。</summary>
    private void UpdateCumulativeY()
    {
        int n = _blockCache.Count;
        if (n == 0)
        {
            _cumulativeY = [0];
            _cachedTotalHeight = _config.ExtraBottomPadding;
            return;
        }
        var cum = new float[n + 1];
        cum[0] = 0;
        for (int i = 0; i < n; i++)
        {
            float h = float.IsNaN(_blockCache[i].CachedHeight) ? 0 : _blockCache[i].CachedHeight;
            cum[i + 1] = cum[i] + h;
        }
        _cumulativeY = cum;
        _cachedTotalHeight = cum[n] + _config.ExtraBottomPadding;
    }

    /// <summary>
    /// 确保当前文档在指定宽度下完成布局。可传入 (scrollY, viewportHeight) 仅布局视口窗口，否则全量布局。
    /// </summary>
    private void EnsureLayout(IDocumentSource doc, float? scrollY = null, float? viewportHeight = null)
    {
        EnsureParsed(doc);
        if (_blockCache.Count == 0) return;

        bool incremental = scrollY.HasValue && viewportHeight.HasValue;
        if (incremental)
        {
            EnsureBlockHeights(doc);
            EnsureLayoutIncremental(doc, scrollY!.Value, viewportHeight!.Value);
        }
        else
        {
            EnsureLayoutFull(doc);
        }
    }

    /// <summary>仅布局与视口相交的块区间（含前后各一屏边距），基于块级缓存与累积 Y。</summary>
    private void EnsureLayoutIncremental(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        int blockCount = _blockCache.Count;
        int maxIndex = _cumulativeY.Length > 0 ? _cumulativeY.Length - 1 : 0;
        if (maxIndex <= 0 || blockCount == 0) return;
        // 保证所有数组长度一致，防止边栏缩放等导致 _cumulativeY / _blockCache / _cachedBlocks / _cachedBlockRanges 不同步
        int safeCount = Math.Min(blockCount, Math.Min(_cachedBlocks.Count, Math.Min(_cachedBlockRanges.Count, maxIndex)));
        if (safeCount <= 0) return;

        const float margin = 800f;
        float yStart = Math.Max(0, scrollY - margin);
        float yEnd = scrollY + viewportHeight + margin;

        int start = 0;
        for (; start < _cumulativeY.Length - 1 && _cumulativeY[start + 1] <= yStart; start++) { }
        int end = start;
        for (; end < _cumulativeY.Length - 1 && _cumulativeY[end] < yEnd; end++) { }
        end = Math.Min(end + 1, safeCount);

        if (start >= end) { start = 0; end = Math.Max(1, safeCount); }

        if (_cachedLayouts != null && _layoutWindow.start == start && _layoutWindow.end == end)
            return;

        _layoutWindow = (start, end);
        float blockIndent = _config.BlockIndent;
        var contentWidth = Math.Max(1, _width - blockIndent);
        float maxRight = contentWidth;
        var layouts = new List<LayoutBlock>();

        for (int blockIndex = start; blockIndex < end; blockIndex++)
        {
            if (blockIndex >= _blockCache.Count || blockIndex >= _cumulativeY.Length
                || blockIndex >= _cachedBlocks.Count || blockIndex >= _cachedBlockRanges.Count)
                continue;
            float topY = _cumulativeY[blockIndex];
            float h = float.IsNaN(_blockCache[blockIndex].CachedHeight) ? 0 : _blockCache[blockIndex].CachedHeight;
            LayoutBlock? layoutBlock = _blockCache[blockIndex].CachedLayout;
            if (layoutBlock == null)
            {
                var ast = _cachedBlocks[blockIndex];
                if (ast == null) continue;
                var (startLine, endLine) = _cachedBlockRanges[blockIndex];
                layoutBlock = _layout.Layout(ast, contentWidth, blockIndex, startLine, endLine);
                _blockCache[blockIndex].CachedLayout = layoutBlock;
                h = layoutBlock.Bounds.Height;
            }
            layoutBlock!.SetGlobalBounds(blockIndent, topY, _width, topY + h);
            layouts.Add(layoutBlock);
            if (layoutBlock.Lines != null)
            {
                foreach (var line in layoutBlock.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        if (run.Bounds.Right > maxRight) maxRight = run.Bounds.Right;
                    }
                }
            }
        }

        _cachedContentWidth = Math.Max(contentWidth, maxRight);
        _cachedLayouts = layouts;
    }

    /// <summary>全量布局，用于命中测试、导出等需要完整布局的场景。基于块级缓存，全部块均做布局并写入缓存。</summary>
    private void EnsureLayoutFull(IDocumentSource doc)
    {
        EnsureBlockHeights(doc);
        if (_blockCache.Count == 0) return;

        if (_layoutWindow.start < 0 && _cachedLayouts != null && _cachedLayouts.Count > 0)
            return;

        _layoutWindow = (-1, -1);
        float blockIndent = _config.BlockIndent;
        var contentWidth = Math.Max(1, _width - blockIndent);
        float maxRight = contentWidth;
        var layouts = new List<LayoutBlock>(_blockCache.Count);

        for (int blockIndex = 0; blockIndex < _cachedBlocks.Count; blockIndex++)
        {
            var ast = _cachedBlocks[blockIndex];
            if (ast == null) continue;

            float topY = _cumulativeY.Length > blockIndex ? _cumulativeY[blockIndex] : 0;
            float h = float.IsNaN(_blockCache[blockIndex].CachedHeight) ? 0 : _blockCache[blockIndex].CachedHeight;

            LayoutBlock? layoutBlock = _blockCache[blockIndex].CachedLayout;
            if (layoutBlock == null)
            {
                var (startLine, endLine) = blockIndex < _cachedBlockRanges.Count ? _cachedBlockRanges[blockIndex] : (0, 0);
                layoutBlock = _layout.Layout(ast, contentWidth, blockIndex, startLine, endLine);
                _blockCache[blockIndex].CachedHeight = layoutBlock.Bounds.Height;
                _blockCache[blockIndex].CachedLayout = layoutBlock;
                h = layoutBlock.Bounds.Height;
            }

            layoutBlock!.SetGlobalBounds(blockIndent, topY, _width, topY + h);
            layouts.Add(layoutBlock);
            if (layoutBlock.Lines != null)
            {
                foreach (var line in layoutBlock.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        if (run.Bounds.Right > maxRight)
                            maxRight = run.Bounds.Right;
                    }
                }
            }
        }

        // 在末尾裁掉所有“完全没有可见行”的尾随块
        float y = _cumulativeY.Length > 0 ? _cumulativeY[_cumulativeY.Length - 1] : 0;
        for (int i = layouts.Count - 1; i >= 0; i--)
        {
            var b = layouts[i];
            if (b.Lines == null || b.Lines.Count == 0)
            {
                y -= b.Bounds.Height;
                layouts.RemoveAt(i);
            }
            else
            {
                break;
            }
        }

        _cachedContentWidth = Math.Max(contentWidth, maxRight);
        _cachedLayouts = layouts;
        _cachedTotalHeight = y + _config.ExtraBottomPadding;
    }

    /// <summary>
    /// 基于 doc.FullText 构建行起始索引，便于从 (line, column) 或块内偏移转换到全局字符偏移。
    /// 仅在文档变更时重建一次。
    /// </summary>
    private void EnsureLineIndex(IDocumentSource doc)
    {
        if (!doc.SupportsRandomAccess)
            return;
        if (_fullTextLineStarts != null && ReferenceEquals(doc, _cachedDoc))
            return;

        var text = doc.FullText;
        if (text.IsEmpty)
        {
            _fullTextLineStarts = new[] { 0 };
            return;
        }

        int estLines = 1 + (text.Length >> 6);
        var list = new List<int>(Math.Max(64, estLines)) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                list.Add(i + 1);
        }
        _fullTextLineStarts = list.ToArray();
    }

    private static (List<MarkdownNode?> blocks, List<(int startLine, int endLine)> ranges) NormalizeFootnotes(
        List<MarkdownNode?> blocks,
        List<(int startLine, int endLine)> ranges)
    {
        var defs = new Dictionary<string, FootnoteDefNode>(StringComparer.Ordinal);
        var refOrder = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteDefNode def && !defs.ContainsKey(def.Id))
                defs[def.Id] = def;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteDefNode)
                continue;
            CollectFootnoteRefs(blocks[i], refOrder, seen);
        }

        if (refOrder.Count == 0)
            return (blocks, ranges);

        var numberById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < refOrder.Count; i++)
            numberById[refOrder[i]] = i + 1;

        var newBlocks = new List<MarkdownNode?>(blocks.Count + 1);
        var newRanges = new List<(int startLine, int endLine)>(ranges.Count + 1);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b is FootnoteDefNode)
                continue;
            newBlocks.Add(ReplaceFootnoteRefs(b, numberById));
            if (i < ranges.Count)
                newRanges.Add(ranges[i]);
            else
                newRanges.Add((0, 0));
        }

        var items = new List<FootnoteEntry>(refOrder.Count);
        foreach (var id in refOrder)
        {
            var num = numberById[id];
            List<MarkdownNode> content;
            if (defs.TryGetValue(id, out var def))
            {
                content = new List<MarkdownNode>(def.Content.Count);
                foreach (var n in def.Content)
                {
                    var replaced = ReplaceFootnoteRefs(n, numberById);
                    if (replaced != null)
                        content.Add(replaced);
                }
                if (content.Count == 0)
                    content = [new ParagraphNode { Content = [new TextNode { Content = "" }] }];
            }
            else
            {
                content = [new ParagraphNode { Content = [new TextNode { Content = "(缺少脚注定义)" }] }];
            }
            items.Add(new FootnoteEntry { Id = id, Number = num, Content = content });
        }

        newBlocks.Add(new FootnoteSectionNode { Items = items });
        newRanges.Add((0, 0));
        return (newBlocks, newRanges);
    }

    private static void CollectFootnoteRefs(MarkdownNode? node, List<string> refOrder, HashSet<string> seen)
    {
        if (node == null) return;
        switch (node)
        {
            case ParagraphNode p:
                CollectFootnoteRefsFromInlines(p.Content, refOrder, seen);
                break;
            case HeadingNode h:
                CollectFootnoteRefsFromInlines(h.Content, refOrder, seen);
                break;
            case BlockquoteNode bq:
                foreach (var c in bq.Children) CollectFootnoteRefs(c, refOrder, seen);
                break;
            case BulletListNode bl:
                foreach (var it in bl.Items) CollectFootnoteRefs(it, refOrder, seen);
                break;
            case OrderedListNode ol:
                foreach (var it in ol.Items) CollectFootnoteRefs(it, refOrder, seen);
                break;
            case ListItemNode li:
                foreach (var c in li.Content) CollectFootnoteRefs(c, refOrder, seen);
                break;
            case DefinitionListNode dl:
                foreach (var item in dl.Items)
                    foreach (var def in item.Definitions)
                        CollectFootnoteRefs(def, refOrder, seen);
                break;
            case DefinitionItemNode di:
                CollectFootnoteRefsFromInlines(di.Term, refOrder, seen);
                foreach (var def in di.Definitions) CollectFootnoteRefs(def, refOrder, seen);
                break;
        }
    }

    /// <summary>
    /// 将块内字符偏移转换为整个 Markdown 文本中的全局字符偏移。
    /// </summary>
    public int? GetDocumentOffsetForBlockPosition(IDocumentSource doc, int blockIndex, int charOffsetInBlock)
    {
        EnsureLayout(doc);
        EnsureLineIndex(doc);
        if (_cachedLayouts == null || _cachedBlockRanges.Count == 0 || _fullTextLineStarts == null)
            return null;
        if (blockIndex < 0 || blockIndex >= _cachedBlockRanges.Count)
            return null;

        var (startLine, _) = _cachedBlockRanges[blockIndex];
        if (startLine < 0 || startLine >= _fullTextLineStarts.Length)
            return null;

        int blockStart = _fullTextLineStarts[startLine];
        return blockStart + Math.Max(0, charOffsetInBlock);
    }

    /// <summary>
    /// 根据列表块索引与块内布局行号，解析出该行在文档中的真实行号（0-based）。
    /// 按「列表行」计数（含 - / * / + 与 [ ] 任务行），与布局一行一项一致，避免与普通列表相连时偏移。
    /// </summary>
    public int? GetTodoSourceLineForListBlock(IDocumentSource doc, int blockIndex, int lineIndexInBlock)
    {
        EnsureParsed(doc);
        if (blockIndex < 0 || blockIndex >= _cachedBlockRanges.Count)
            return null;
        var ast = _cachedBlocks[blockIndex];
        if (ast is not BulletListNode and not OrderedListNode)
            return null;

        var (startLine, endLine) = _cachedBlockRanges[blockIndex];
        if (startLine < 0 || endLine > doc.LineCount)
            return null;

        int count = 0;
        for (int i = startLine; i < endLine; i++)
        {
            if (!IsListLine(doc.GetLine(i)))
                continue;
            if (count == lineIndexInBlock)
                return i;
            count++;
        }
        return null;
    }

    /// <summary>是否为任意列表行（无序 - * + 或有序 N. 或任务 [ ] / [x]），用于与布局行一一对应。</summary>
    private static bool IsListLine(ReadOnlySpan<char> line)
    {
        line = line.TrimStart();
        if (line.Length < 2) return false;
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
            return true;
        if (line.StartsWith("- [ ]", StringComparison.Ordinal) || line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("* [ ]", StringComparison.Ordinal) || line.StartsWith("* [x]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("+ [ ]", StringComparison.Ordinal) || line.StartsWith("+ [x]", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line[0] >= '0' && line[0] <= '9')
        {
            int i = 1;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
            if (i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ')
                return true;
        }
        return false;
    }

    private static void CollectFootnoteRefsFromInlines(List<InlineNode> inlines, List<string> refOrder, HashSet<string> seen)
    {
        foreach (var n in inlines)
        {
            if (n is FootnoteRefNode fn && seen.Add(fn.Id))
                refOrder.Add(fn.Id);
            else if (n is BoldNode bn)
                CollectFootnoteRefsFromInlines(bn.Content, refOrder, seen);
            else if (n is ItalicNode inv)
                CollectFootnoteRefsFromInlines(inv.Content, refOrder, seen);
            else if (n is StrikethroughNode sn)
                CollectFootnoteRefsFromInlines(sn.Content, refOrder, seen);
        }
    }

    private static MarkdownNode? ReplaceFootnoteRefs(MarkdownNode? node, IReadOnlyDictionary<string, int> numberById)
    {
        if (node == null) return null;
        switch (node)
        {
            case ParagraphNode p:
                return new ParagraphNode { Content = ReplaceFootnoteRefsInlines(p.Content, numberById) };
            case HeadingNode h:
                return new HeadingNode { Level = h.Level, Content = ReplaceFootnoteRefsInlines(h.Content, numberById) };
            case BlockquoteNode bq:
                var bqChildren = new List<MarkdownNode>(bq.Children.Count);
                foreach (var n in bq.Children)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) bqChildren.Add(r);
                }
                return new BlockquoteNode { Children = bqChildren };
            case BulletListNode bl:
                var blItems = new List<ListItemNode>(bl.Items.Count);
                foreach (var it in bl.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is ListItemNode li) blItems.Add(li);
                }
                return new BulletListNode { Items = blItems };
            case OrderedListNode ol:
                var olItems = new List<ListItemNode>(ol.Items.Count);
                foreach (var it in ol.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is ListItemNode oli) olItems.Add(oli);
                }
                return new OrderedListNode { StartNumber = ol.StartNumber, Items = olItems };
            case ListItemNode li:
                var liContent = new List<MarkdownNode>(li.Content.Count);
                foreach (var n in li.Content)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) liContent.Add(r);
                }
                return new ListItemNode { IsTask = li.IsTask, IsChecked = li.IsChecked, Content = liContent };
            case DefinitionListNode dl:
                var dlItems = new List<DefinitionItemNode>(dl.Items.Count);
                foreach (var it in dl.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is DefinitionItemNode di) dlItems.Add(di);
                }
                return new DefinitionListNode { Items = dlItems };
            case DefinitionItemNode di:
                var diDefs = new List<MarkdownNode>(di.Definitions.Count);
                foreach (var n in di.Definitions)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) diDefs.Add(r);
                }
                return new DefinitionItemNode { Term = ReplaceFootnoteRefsInlines(di.Term, numberById), Definitions = diDefs };
            default:
                return node;
        }
    }

    private static List<InlineNode> ReplaceFootnoteRefsInlines(List<InlineNode> inlines, IReadOnlyDictionary<string, int> numberById)
    {
        var list = new List<InlineNode>(inlines.Count);
        foreach (var n in inlines)
        {
            switch (n)
            {
                case FootnoteRefNode fn:
                    var num = numberById.TryGetValue(fn.Id, out var v) ? v : 0;
                    list.Add(new FootnoteMarkerNode { Id = fn.Id, Number = num });
                    break;
                case BoldNode bn:
                    list.Add(new BoldNode { Content = ReplaceFootnoteRefsInlines(bn.Content, numberById) });
                    break;
                case ItalicNode inv:
                    list.Add(new ItalicNode { Content = ReplaceFootnoteRefsInlines(inv.Content, numberById) });
                    break;
                case StrikethroughNode sn:
                    list.Add(new StrikethroughNode { Content = ReplaceFootnoteRefsInlines(sn.Content, numberById) });
                    break;
                default:
                    list.Add(n);
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// 获取选中文本（支持跨块，多行含换行）
    /// </summary>
    public string GetSelectedText(IDocumentSource doc, SelectionRange sel)
    {
        if (sel.IsEmpty) return "";
        EnsureLayout(doc);
        var (startBlock, startOff, endBlock, endOff) = (sel.StartBlock, sel.StartOffset, sel.EndBlock, sel.EndOffset);
        if (startBlock > endBlock || (startBlock == endBlock && startOff > endOff))
            (startBlock, startOff, endBlock, endOff) = (endBlock, endOff, startBlock, startOff);

        var sb = new System.Text.StringBuilder();
        bool firstBlock = true;

        if (_cachedLayouts == null)
            return "";

        foreach (var layoutBlock in _cachedLayouts)
        {
            var bi = layoutBlock.BlockIndex;
            if (bi < startBlock || bi > endBlock)
                continue;

            if (!firstBlock)
                sb.Append('\n');
            firstBlock = false;

            int blockStart = bi == startBlock ? startOff : 0;
            int blockEnd = bi == endBlock ? endOff : int.MaxValue;
            bool firstLine = true;

            foreach (var layoutLine in layoutBlock.Lines)
            {
                if (!firstLine) sb.Append('\n');
                firstLine = false;
                foreach (var run in layoutLine.Runs)
                {
                    int runStart = run.CharOffset;
                    int runEnd = run.CharOffset + run.Text.Length;
                    int oStart = Math.Max(runStart, blockStart);
                    int oEnd = Math.Min(runEnd, blockEnd);
                    if (oStart < oEnd)
                        sb.Append(run.Text.AsSpan(oStart - runStart, oEnd - oStart));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>文档总高度（ScrollViewer 内容高度）。由各块实际高度聚合，无估计/实际切换。</summary>
    public float MeasureTotalHeight(IDocumentSource doc)
    {
        EnsureParsed(doc);
        if (_blockCache.Count == 0)
            return _config.ExtraBottomPadding;
        EnsureBlockHeights(doc);
        return _cachedTotalHeight;
    }

    /// <summary>强制全量布局（导出、命中测试等需要完整布局时由调用方显式调用）。</summary>
    public void EnsureFullLayout(IDocumentSource doc)
    {
        EnsureLayout(doc);
    }

    /// <summary>
    /// 计算内容实际需要的宽度（含代码块等长行）。有布局缓存时返回缓存值，否则返回当前宽度避免触发全量布局。
    /// </summary>
    public float MeasureContentWidth(IDocumentSource doc)
    {
        EnsureParsed(doc);
        if (_cachedLayouts != null && _cachedContentWidth > 0)
            return _cachedContentWidth;
        return Math.Max(1, _width - _config.BlockIndent) + _config.BlockIndent;
    }

    /// <summary>脚注区顶部在内容坐标系中的 Y，用于点击脚注上标后滚动。无脚注时返回 null。</summary>
    public float? GetContentYForFootnoteSection(IDocumentSource doc)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null) return null;
        foreach (var b in _cachedLayouts)
        {
            if (b.Kind == BlockKind.Footnotes)
                return b.Bounds.Top;
        }
        return null;
    }

    /// <summary>按脚注 id 查找正文中第一次出现的脚注引用位置（用于 ↑ 回链）。</summary>
    public float? GetContentYForFirstFootnoteRefId(IDocumentSource doc, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLayout(doc);
        if (_cachedLayouts == null) return null;
        foreach (var block in _cachedLayouts)
        {
            foreach (var line in block.Lines)
            {
                foreach (var run in line.Runs)
                {
                    if (run.Style == RunStyle.FootnoteRef && string.Equals(run.FootnoteRefId, id, StringComparison.Ordinal))
                        return block.Bounds.Top + line.Y + run.Bounds.Top;
                }
            }
        }
        return null;
    }

    /// <summary>指定块内字符偏移对应的内容 Y（用于 ↩︎ 回链滚动）。</summary>
    public float? GetContentYForBlockOffset(IDocumentSource doc, int blockIndex, int charOffset)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null) return null;
        foreach (var block in _cachedLayouts)
        {
            if (block.BlockIndex != blockIndex) continue;
            foreach (var line in block.Lines)
            {
                foreach (var run in line.Runs)
                {
                    int end = run.CharOffset + run.Text.Length;
                    if (charOffset >= run.CharOffset && charOffset <= end)
                        return block.Bounds.Top + line.Y + run.Bounds.Top;
                }
            }
            return block.Bounds.Top;
        }
        return null;
    }
}
