using System.Threading;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 可选的布局性能计数器：将 <see cref="Enabled"/> 设为 true 后测量 Skia 文本测量次数、
/// ComputeSlim 累积高度重建步数等，便于对比优化前后（单元测试或本地剖析）。
/// 默认关闭，计数为原子操作，开销在关闭时接近零（单次 bool 检查）。
/// </summary>
public static class LayoutDiagnostics
{
    private static long _skiaMeasureTextCalls;
    private static long _computeSlimCumulativePasses;

    /// <summary>开启后递增各类计数器。</summary>
    public static bool Enabled { get; set; }

    /// <summary>SkiaLayoutEngine 内 <c>MeasureText</c> / 等价测量调用次数（含前缀表构建中的逐前缀测量）。</summary>
    public static long SkiaMeasureTextCalls => Volatile.Read(ref _skiaMeasureTextCalls);

    /// <summary>每次 <see cref="LayoutComputeService.ComputeSlim"/> 结束时对整份 cum 做前缀重建的次数（优化后每调用 1 次）。</summary>
    public static long ComputeSlimCumulativePasses => Volatile.Read(ref _computeSlimCumulativePasses);

    public static void OnSkiaMeasureText()
    {
        if (Enabled)
            Interlocked.Increment(ref _skiaMeasureTextCalls);
    }

    public static void OnComputeSlimCumulativePass()
    {
        if (Enabled)
            Interlocked.Increment(ref _computeSlimCumulativePasses);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _skiaMeasureTextCalls, 0);
        Interlocked.Exchange(ref _computeSlimCumulativePasses, 0);
    }
}
