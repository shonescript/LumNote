using DiffPlex.DiffBuilder.Model;
using MarkdownEditor.Services;
using Xunit;

namespace MarkdownEditor.Tests;

public class SideBySideDiffServiceTests
{
    [Fact]
    public void Build_aligns_lines_and_marks_change()
    {
        var r = SideBySideDiffService.Build("a\nb\nc", "a\nx\nc");
        Assert.NotEmpty(r.LeftLineTypes);
        Assert.Equal(r.LeftLineTypes.Count, r.RightLineTypes.Count);
        var idx = Array.IndexOf(r.LeftText.Split(Environment.NewLine), "b");
        Assert.True(idx >= 0);
        // 单行替换可能标记为 Modified（与 Deleted+Inserted 等价语义）
        Assert.Equal(r.LeftLineTypes[idx], r.RightLineTypes[idx]);
        Assert.NotEqual(ChangeType.Unchanged, r.LeftLineTypes[idx]);
    }
}
