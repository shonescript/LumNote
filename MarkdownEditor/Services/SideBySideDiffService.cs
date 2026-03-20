using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace MarkdownEditor.Services;

/// <summary>
/// 使用 DiffPlex 生成左右对齐的侧栏 diff 文本与每行 <see cref="ChangeType"/>（与 Git 行级 diff 同类算法）。
/// </summary>
public static class SideBySideDiffService
{
    private static readonly SideBySideDiffBuilder Builder = new(new Differ());

    /// <summary>构建侧栏 diff：左右行数一致，空白行为对齐占位。</summary>
    public static SideBySideDiffResult Build(string? oldText, string? newText)
    {
        oldText ??= "";
        newText ??= "";
        var model = Builder.BuildDiffModel(oldText, newText);
        var leftTypes = new List<ChangeType>(model.OldText.Lines.Count);
        var rightTypes = new List<ChangeType>(model.NewText.Lines.Count);
        var leftLines = new List<string>(model.OldText.Lines.Count);
        var rightLines = new List<string>(model.NewText.Lines.Count);
        foreach (var line in model.OldText.Lines)
        {
            leftLines.Add(line.Text ?? "");
            leftTypes.Add(line.Type);
        }
        foreach (var line in model.NewText.Lines)
        {
            rightLines.Add(line.Text ?? "");
            rightTypes.Add(line.Type);
        }
        var sep = Environment.NewLine;
        return new SideBySideDiffResult(
            string.Join(sep, leftLines),
            string.Join(sep, rightLines),
            leftTypes,
            rightTypes);
    }
}

/// <param name="LeftText">左侧（旧版）完整文本。</param>
/// <param name="RightText">右侧（新版）完整文本。</param>
/// <param name="LeftLineTypes">与左侧每一行对应的变更类型（1-based 行号与文档一致）。</param>
/// <param name="RightLineTypes">与右侧每一行对应的变更类型。</param>
public sealed record SideBySideDiffResult(
    string LeftText,
    string RightText,
    IReadOnlyList<ChangeType> LeftLineTypes,
    IReadOnlyList<ChangeType> RightLineTypes);
