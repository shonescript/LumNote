using System;
using System.Threading.Tasks;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 布局任务调度器：负责在后台线程上执行布局计算，并在完成后通过回调返回 LayoutBlocksSnapshot。
/// 版本号用于 <see cref="EnqueueLayoutFromBlocks"/> 中“只应用最新结果”的过滤（避免过时布局覆盖新布局）。
/// </summary>
public sealed class LayoutTaskScheduler
{
    private readonly object _lock = new();
    private long _version;

    /// <summary>
    /// 提交一次“解析+布局”任务。
    /// computeSnapshot 由调用方提供，内部应是纯计算（不触碰 UI 控件），
    /// onCompleted 在 UI 线程外部调用方自行决定是否切回 UI 线程。
    /// </summary>
    public void EnqueueParseAndLayout(
        IDocumentSource docSnapshot,
        Func<IDocumentSource, LayoutBlocksSnapshot> computeSnapshot,
        Action<LayoutBlocksSnapshot>? onCompleted
    )
    {
        if (computeSnapshot == null)
            throw new ArgumentNullException(nameof(computeSnapshot));

        long myVersion;
        lock (_lock)
        {
            _version++;
            myVersion = _version;
        }

        _ = Task.Run(() =>
            {
                try
                {
                    var snapshot = computeSnapshot(docSnapshot);
                    onCompleted?.Invoke(snapshot);
                }
                catch (Exception)
                {
                    // 忽略异常，避免未观察任务异常
                }
            })
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        try
                        {
                            _ = t.Exception;
                        }
                        catch { }
                    }
                },
                TaskContinuationOptions.OnlyOnFaulted
            );
    }

    /// <summary>
    /// 从 BlockListSnapshot 驱动后台布局计算。
    /// 自动做脚注归一化后调用 LayoutComputeService。
    /// 使用版本号：完成时若 version 与当前版本不符则忽略结果，避免过时结果覆盖新布局。
    /// </summary>
    /// <param name="previousCum">上一帧的累积高度数组，用于 ComputeSlim 保持布局一致性；长度不匹配时自动忽略。</param>
    /// <param name="onCompleted">(snapshot, version)，调用方需检查 version 是否仍为最新再应用。</param>
    public void EnqueueLayoutFromBlocks(
        BlockListSnapshot rawBlockSnapshot,
        float width,
        float? scrollY,
        float? viewportHeight,
        ILayoutEngine layoutEngine,
        EngineConfig config,
        float[]? previousCum,
        Action<LayoutBlocksSnapshot, long>? onCompleted
    )
    {
        if (rawBlockSnapshot == null)
            throw new ArgumentNullException(nameof(rawBlockSnapshot));
        if (layoutEngine == null)
            throw new ArgumentNullException(nameof(layoutEngine));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        var (blocks, ranges) = RenderEngine.NormalizeBlockSnapshot(rawBlockSnapshot);

        long myVersion;
        lock (_lock)
        {
            _version++;
            myVersion = _version;
        }
        _ = Task.Run(() =>
            {
                try
                {
                    LayoutBlocksSnapshot snapshot;
                    if (scrollY.HasValue && viewportHeight.HasValue && viewportHeight.Value > 0)
                    {
                        // 统一使用 ComputeSlim 作为渲染路径：无论文档长短，均基于可见窗口做增量布局。
                        // 对于较短文档，预渲染 margin 会覆盖全部块，相当于“整篇缓存”；对于超长文档，仅缓存可见附近区域。
                        snapshot = LayoutComputeService.ComputeSlim(
                            blocks,
                            ranges,
                            width,
                            scrollY.Value,
                            viewportHeight.Value,
                            layoutEngine,
                            config,
                            previousCum
                        );
                    }
                    else
                    {
                        // 无有效滚动信息时（如导出场景）回落到完整布局
                        snapshot = LayoutComputeService.ComputeFull(
                            blocks,
                            ranges,
                            width,
                            layoutEngine,
                            config
                        );
                    }

                    onCompleted?.Invoke(snapshot, myVersion);
                }
                catch (Exception)
                {
                    // 忽略异常
                }
            })
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        try
                        {
                            _ = t.Exception;
                        }
                        catch { }
                    }
                },
                TaskContinuationOptions.OnlyOnFaulted
            );
    }

    /// <summary>当前版本号，用于判断回调结果是否仍有效。</summary>
    public long CurrentVersion
    {
        get
        {
            lock (_lock)
                return _version;
        }
    }
}
