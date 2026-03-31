using System;
using System.Collections.Generic;
using MarkdownEditor.Engine.Layout;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 全量布局下按文档纵轴的栅格化瓦片缓存（SKImage + LRU），减轻重复滚动时的 CPU 绘制。
/// 仅在 <see cref="RenderEngine"/> 判定为整篇布局（非 ComputeSlim 窗口）时使用。
/// </summary>
public sealed class ViewportTileRasterCache : IDisposable
{
    private sealed class Entry
    {
        public SKImage Image = null!;
        public ulong Signature;
        public LinkedListNode<int>? LruNode;
    }

    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, Entry> _map = new();
    private int _tileHeightPx = 512;
    private int _maxEntries = 64;

    public void Configure(int tileHeightPx, int maxEntries)
    {
        _tileHeightPx = Math.Max(64, tileHeightPx);
        _maxEntries = Math.Max(4, maxEntries);
    }

    public void Clear()
    {
        foreach (var kv in _map)
            kv.Value.Image.Dispose();
        _map.Clear();
        _lru.Clear();
    }

    public void Dispose() => Clear();

    /// <summary>使与文档纵轴区间 [docY0, docY1) 相交的瓦片失效。</summary>
    public void InvalidateDocYRange(float docY0, float docY1)
    {
        if (_map.Count == 0 || docY1 <= docY0)
            return;
        int i0 = (int)Math.Floor(docY0 / _tileHeightPx);
        int i1 = (int)Math.Floor((docY1 - 1e-3f) / _tileHeightPx);
        for (int i = i0; i <= i1; i++)
            RemoveTile(i);
    }

    /// <summary>文档块索引 [blockStartInclusive, blockEndExclusive) 对应的纵轴范围失效。</summary>
    public void InvalidateDocumentBlockRange(float[] cum, int blockStartInclusive, int blockEndExclusive)
    {
        if (_map.Count == 0 || cum == null || cum.Length < 2)
            return;
        if (blockStartInclusive >= blockEndExclusive)
        {
            Clear();
            return;
        }
        int n = cum.Length - 1;
        int bs = Math.Clamp(blockStartInclusive, 0, n - 1);
        int be = Math.Clamp(blockEndExclusive, 0, n);
        if (be <= bs)
            return;
        float y0 = cum[bs];
        float y1 = cum[be];
        InvalidateDocYRange(y0, y1);
    }

    private void RemoveTile(int tileIndex)
    {
        if (!_map.TryGetValue(tileIndex, out var e))
            return;
        if (e.LruNode != null)
            _lru.Remove(e.LruNode);
        e.Image.Dispose();
        _map.Remove(tileIndex);
    }

    private void TouchLru(int tileIndex, Entry e)
    {
        if (e.LruNode != null)
            _lru.Remove(e.LruNode);
        e.LruNode = _lru.AddLast(tileIndex);
    }

    private void EnforceCapacity()
    {
        while (_map.Count > _maxEntries && _lru.First != null)
            RemoveTile(_lru.First.Value);
    }

    private static ulong ComputeTileSignature(
        int tileIndex,
        int tileHeightPx,
        float drawWidth,
        IReadOnlyList<LayoutBlock> layouts,
        int startLayoutIndex,
        int endLayoutExclusive
    )
    {
        var hc = new HashCode();
        hc.Add(tileIndex);
        hc.Add(tileHeightPx);
        hc.Add(BitConverter.SingleToInt32Bits(drawWidth));
        for (int i = startLayoutIndex; i < endLayoutExclusive; i++)
        {
            var b = layouts[i];
            hc.Add(b.BlockIndex);
            hc.Add(SkiaRenderer.ComputeBlockLayoutContentHash(b));
        }
        return (ulong)(uint)hc.ToHashCode();
    }

    /// <summary>
    /// 在已 <c>Translate(0,-scrollY)</c> 的画布上绘制视口内瓦片；返回 false 时应回退到常规按块绘制。
    /// </summary>
    public bool TryRenderTiled(
        SKCanvas canvas,
        SkiaRenderer renderer,
        IReadOnlyList<LayoutBlock> layouts,
        float scrollY,
        float viewportHeight,
        float drawWidth,
        float contentMaxRight,
        SKColor pageBackground,
        float scale
    )
    {
        if (layouts.Count == 0 || viewportHeight <= 0)
            return false;

        int iw = (int)Math.Ceiling((double)Math.Max(drawWidth, contentMaxRight));
        iw = Math.Max(1, iw);
        int th = _tileHeightPx;

        using (var probe = SKSurface.Create(new SKImageInfo(iw, th, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            if (probe == null)
                return false;
        }

        int firstTile = (int)Math.Floor(scrollY / th);
        int lastTile = (int)Math.Floor((scrollY + viewportHeight - 1e-3f) / th);

        for (int ti = firstTile; ti <= lastTile; ti++)
        {
            float tileY = ti * th;

            int si = -1;
            int ei = -1;
            for (int i = 0; i < layouts.Count; i++)
            {
                var b = layouts[i];
                if (b.Bounds.Bottom <= tileY)
                    continue;
                if (b.Bounds.Top >= tileY + th)
                    break;
                if (si < 0)
                    si = i;
                ei = i + 1;
            }

            ulong sig = si < 0 || ei <= si
                ? ComputeTileSignature(ti, th, drawWidth, layouts, 0, 0)
                : ComputeTileSignature(ti, th, drawWidth, layouts, si, ei);

            if (si < 0 || ei <= si)
            {
                // 空条带：仅背景
                if (_map.TryGetValue(ti, out var emptyPrev))
                {
                    if (emptyPrev.Signature == sig)
                    {
                        TouchLru(ti, emptyPrev);
                        using var p = new SKPaint();
                        canvas.DrawImage(emptyPrev.Image, 0, tileY, p);
                        continue;
                    }
                    RemoveTile(ti);
                }

                using var surfEmpty = SKSurface.Create(new SKImageInfo(iw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (surfEmpty == null)
                    return false;
                surfEmpty.Canvas.Clear(pageBackground);
                var imgEmpty = surfEmpty.Snapshot();
                EnforceCapacity();
                var nodeEmpty = _lru.AddLast(ti);
                _map[ti] = new Entry { Image = imgEmpty, Signature = sig, LruNode = nodeEmpty };
                using var pe = new SKPaint();
                canvas.DrawImage(imgEmpty, 0, tileY, pe);
                continue;
            }

            if (_map.TryGetValue(ti, out var entry) && entry.Signature == sig)
            {
                TouchLru(ti, entry);
                using var p = new SKPaint();
                canvas.DrawImage(entry.Image, 0, tileY, p);
                continue;
            }

            if (entry != null)
                RemoveTile(ti);

            using var surface = SKSurface.Create(new SKImageInfo(iw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null)
                return false;

            surface.Canvas.Clear(pageBackground);
            surface.Canvas.Save();
            surface.Canvas.Translate(0, -tileY);
            var tileCtx = new TileSkiaContext(surface.Canvas, new SKSize(iw, th), scale);
            renderer.Render(tileCtx, layouts, si, ei - si, null);
            surface.Canvas.Restore();

            var image = surface.Snapshot();
            EnforceCapacity();
            var node = _lru.AddLast(ti);
            _map[ti] = new Entry { Image = image, Signature = sig, LruNode = node };
            using var paint = new SKPaint();
            canvas.DrawImage(image, 0, tileY, paint);
        }

        return true;
    }

    private sealed class TileSkiaContext : ISkiaRenderContext
    {
        public TileSkiaContext(SKCanvas canvas, SKSize size, float scale)
        {
            Canvas = canvas;
            Size = size;
            Scale = scale;
        }

        public SKCanvas Canvas { get; }
        public SKSize Size { get; }
        public float Scale { get; }
    }
}
