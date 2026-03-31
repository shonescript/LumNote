using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Engine.Render;
using Xunit;

namespace MarkdownEditor.Tests;

public class LayoutPerformanceAndComputeTests
{
    private static ParagraphNode P(string text) =>
        new() { Content = [new TextNode { Content = text }] };

    [Fact]
    public void ComputeSlim_FullWindow_CumulativeY_Aligns_With_ComputeFull()
    {
        var config = new EngineConfig { BaseFontSize = 16, LineSpacing = 1.35f };
        var renderer = new SkiaRenderer(config);
        var layout = new SkiaLayoutEngine(config, null, renderer);

        var blocks = new List<MarkdownNode?> { P("Hello world."), P("Second paragraph line."), P("Third.") };
        var ranges = new List<(int, int)> { (0, 1), (1, 2), (2, 3) };
        const float width = 420f;

        var full = LayoutComputeService.ComputeFull(blocks, ranges, width, layout, config);
        var slim = LayoutComputeService.ComputeSlim(
            blocks,
            ranges,
            width,
            scrollY: 0,
            viewportHeight: 50_000f,
            layout,
            config,
            previousCum: null);

        Assert.Equal(full.CumulativeY.Count, slim.CumulativeY.Count);
        for (int i = 0; i < full.CumulativeY.Count; i++)
            Assert.Equal(full.CumulativeY[i], slim.CumulativeY[i], precision: 3);
    }

    [Fact]
    public void LayoutDiagnostics_ComputeSlim_Records_Single_Cumulative_Pass()
    {
        LayoutDiagnostics.Reset();
        LayoutDiagnostics.Enabled = true;
        try
        {
            var config = new EngineConfig();
            var renderer = new SkiaRenderer(config);
            var layout = new SkiaLayoutEngine(config, null, renderer);
            var blocks = new List<MarkdownNode?> { P("a"), P("b") };
            var ranges = new List<(int, int)> { (0, 1), (1, 2) };

            LayoutComputeService.ComputeSlim(
                blocks,
                ranges,
                300f,
                0,
                40_000f,
                layout,
                config,
                null);

            Assert.Equal(1, LayoutDiagnostics.ComputeSlimCumulativePasses);
        }
        finally
        {
            LayoutDiagnostics.Enabled = false;
            LayoutDiagnostics.Reset();
        }
    }

    [Fact]
    public void BreakTextToFit_PrefixCache_SecondLayout_Reuses_Width_Table()
    {
        LayoutDiagnostics.Reset();
        LayoutDiagnostics.Enabled = true;
        try
        {
            var config = new EngineConfig();
            var renderer = new SkiaRenderer(config);
            var layout = new SkiaLayoutEngine(config, null, renderer);
            var longText = new string('x', 60) + " " + new string('y', 60);
            var blocks = new List<MarkdownNode?> { P(longText) };
            var ranges = new List<(int, int)> { (0, 1) };

            long m0 = LayoutDiagnostics.SkiaMeasureTextCalls;
            LayoutComputeService.ComputeFull(blocks, ranges, 200f, layout, config);
            long cold = LayoutDiagnostics.SkiaMeasureTextCalls - m0;

            LayoutComputeService.ComputeFull(blocks, ranges, 280f, layout, config);
            long warm = LayoutDiagnostics.SkiaMeasureTextCalls - m0 - cold;

            Assert.True(warm < cold,
                "Second layout at different width should measure fewer times thanks to prefix width cache.");
        }
        finally
        {
            LayoutDiagnostics.Enabled = false;
            LayoutDiagnostics.Reset();
        }
    }
}
