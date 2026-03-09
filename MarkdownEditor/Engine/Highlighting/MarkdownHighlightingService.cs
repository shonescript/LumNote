using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Highlighting;

/// <summary>
/// 基于现有 BlockScanner + MarkdownParser 的 Markdown 语法高亮分析器。
/// 为了先消除编译错误并提供基础高亮，这里采用“整文解析 + 行内简单映射”的实现，
/// 后续可以按需做增量/按块优化。
/// </summary>
public sealed class MarkdownHighlightingService
{
    /// <summary>
    /// 对整篇或指定行范围的 Markdown 文本进行语法高亮分析。
    /// visibleStartLine / visibleEndLine 为可见行号（0-based，含），用于在大文档中只分析可见窗口附近的块。
    /// 传入 null 时则扫描整篇文档。
    /// </summary>
    public IReadOnlyList<HighlightToken> Analyze(string text, int? visibleStartLine = null, int? visibleEndLine = null)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<HighlightToken>();

        var docSource = new StringDocumentSource(text);
        var tokens = new List<HighlightToken>();

        int line = 0;
        while (line < docSource.LineCount)
        {
            var span = BlockScanner.ScanNextBlock(docSource, line);
            if (span.LineCount <= 0)
                break;

            // 若指定了可见窗口，则仅对与窗口有交集的块做分析，避免在大文档中对完全不可见块反复解析。
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                int start = visibleStartLine.Value;
                int end = visibleEndLine.Value;
                if (span.EndLine <= start)
                {
                    line = span.EndLine;
                    continue;
                }
                if (span.StartLine > end)
                    break;
            }

            AddBlockTokens(docSource, span, tokens, visibleStartLine, visibleEndLine);
            line = span.EndLine;
        }

        return tokens;
    }

    private static void AddBlockTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        switch (span.Kind)
        {
            case BlockKind.Heading:
                AddHeadingTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.CodeBlock:
                AddCodeBlockTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.List:
                AddListTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.Blockquote:
                AddBlockquoteTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.Table:
                AddTableTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.HorizontalRule:
                AddHorizontalRuleTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.MathBlock:
                AddMathBlockTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            case BlockKind.Paragraph:
                AddParagraphInlineTokens(doc, span, output, visibleStartLine, visibleEndLine);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 表格块高亮：
    /// - 首行表头及分隔行中的 '|' 和 ':' 使用 TableSeparator 颜色；
    /// - 单元格内容继续按行内规则扫描（粗体/链接/行内代码/行内数学等）。
    /// </summary>
    private static void AddTableTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        if (span.LineCount <= 0)
            return;

        int headerLine = span.StartLine;
        int separatorLine = span.StartLine + 1 < span.EndLine ? span.StartLine + 1 : -1;

        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var lineSpan = doc.GetLine(i).ToString();
            if (lineSpan.Length == 0)
                continue;

            // 1) 竖线和对齐冒号高亮为 TableSeparator
            for (int col = 0; col < lineSpan.Length; col++)
            {
                char ch = lineSpan[col];
                if (ch == '|' || (i == separatorLine && ch == ':'))
                {
                    output.Add(new HighlightToken(i, col, 1, HighlightKind.TableSeparator));
                }
            }

            // 2) 单元格内容行内高亮：
            //    - 对分隔行，仅做竖线/冒号高亮，不再重复扫描；
            //    - 其他行按普通行内语法扫描。
            if (i == separatorLine)
                continue;

            ScanLineInline(lineSpan, i, 0, output);
        }
    }

    private static void AddHeadingTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        if (visibleStartLine.HasValue && visibleEndLine.HasValue)
        {
            if (span.StartLine < visibleStartLine.Value || span.StartLine > visibleEndLine.Value)
                return;
        }

        var lineSpan = doc.GetLine(span.StartLine);
        if (lineSpan.Length == 0)
            return;
        int hashCount = 0;
        while (hashCount < lineSpan.Length && lineSpan[hashCount] == '#')
            hashCount++;
        if (hashCount == 0)
            return;

        // '#' 本身当作普通文本，后面的内容标为 Heading
        int textStart = hashCount;
        while (textStart < lineSpan.Length && lineSpan[textStart] == ' ')
            textStart++;
        if (textStart < lineSpan.Length)
        {
            output.Add(new HighlightToken(span.StartLine, textStart, lineSpan.Length - textStart, HighlightKind.Heading));
        }
        AddHeadingInlineTokens(doc, span, output, visibleStartLine, visibleEndLine);
    }

    private static void AddCodeBlockTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i);
            if (line.Length == 0)
                continue;
            // 整行作为 CodeBlock 处理
            output.Add(new HighlightToken(i, 0, line.Length, HighlightKind.CodeBlock));
        }
    }

    private static void AddListTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i);
            if (line.Length == 0)
                continue;
            var trimmed = line.TrimStart();
            int leadingSpaces = line.Length - trimmed.Length;
            if (trimmed.Length == 0)
                continue;

            // 无序/有序/任务列表前缀长度（与 MarkdownParser 一致）
            int markerLen = 0;
            if (trimmed.StartsWith("- [ ]") || trimmed.StartsWith("* [ ]") || trimmed.StartsWith("+ [ ]"))
                markerLen = 5;
            else if (trimmed.StartsWith("- [x]") || trimmed.StartsWith("- [X]") || trimmed.StartsWith("* [x]") || trimmed.StartsWith("* [X]") || trimmed.StartsWith("+ [x]") || trimmed.StartsWith("+ [X]"))
                markerLen = 6;
            else if ((trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed.Length > 1 && (trimmed[1] == ' ' || trimmed[1] == '\t'))
                markerLen = 2;
            else
            {
                int j = 0;
                while (j < trimmed.Length && char.IsDigit(trimmed[j])) j++;
                if (j > 0 && j + 1 < trimmed.Length && (trimmed[j] == '.' || trimmed[j] == ')') && (trimmed[j + 1] == ' ' || trimmed[j + 1] == '\t'))
                    markerLen = j + 2;
            }

            if (markerLen > 0)
            {
                output.Add(new HighlightToken(i, leadingSpaces, markerLen, HighlightKind.ListMarker));
            }
            int contentStart = leadingSpaces + markerLen;
            while (contentStart < line.Length && (line[contentStart] == ' ' || line[contentStart] == '\t'))
                contentStart++;
            if (contentStart < line.Length)
                ScanLineInline(line.ToString(), i, contentStart, output);
        }
    }

    private static void AddBlockquoteTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i);
            if (line.Length == 0)
                continue;
            int idx = 0;
            while (idx < line.Length && char.IsWhiteSpace(line[idx]))
                idx++;
            if (idx < line.Length && line[idx] == '>')
            {
                output.Add(new HighlightToken(i, idx, 1, HighlightKind.BlockquoteMarker));
                int contentStart = idx + 1;
                while (contentStart < line.Length && (line[contentStart] == ' ' || line[contentStart] == '\t'))
                    contentStart++;
                if (contentStart < line.Length)
                    ScanLineInline(line.ToString(), i, contentStart, output);
            }
        }
    }

    private static void AddHorizontalRuleTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i);
            if (line.Length == 0) continue;
            output.Add(new HighlightToken(i, 0, line.Length, HighlightKind.HorizontalRule));
        }
    }

    private static void AddMathBlockTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i);
            if (line.Length == 0) continue;
            output.Add(new HighlightToken(i, 0, line.Length, HighlightKind.MathBlock));
        }
    }

    /// <summary>
    /// 段落块：对每一行做行内语法扫描，精确到每个关键字符，与原文完全对齐。
    /// </summary>
    private static void AddParagraphInlineTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (visibleStartLine.HasValue && visibleEndLine.HasValue)
            {
                if (i < visibleStartLine.Value || i > visibleEndLine.Value)
                    continue;
            }

            var line = doc.GetLine(i).ToString();
            ScanLineInline(line, i, 0, output);
        }
    }

    /// <summary>
    /// 标题行：标题文字后可能还有行内语法（粗体、链接等），对整行做行内扫描；扫描起点为 # 和空格之后。
    /// </summary>
    private static void AddHeadingInlineTokens(
        IDocumentSource doc,
        BlockSpan span,
        List<HighlightToken> output,
        int? visibleStartLine,
        int? visibleEndLine)
    {
        if (visibleStartLine.HasValue && visibleEndLine.HasValue)
        {
            if (span.StartLine < visibleStartLine.Value || span.StartLine > visibleEndLine.Value)
                return;
        }

        var line = doc.GetLine(span.StartLine).ToString();
        int pos = 0;
        while (pos < line.Length && line[pos] == '#') pos++;
        while (pos < line.Length && line[pos] == ' ') pos++;
        if (pos < line.Length)
            ScanLineInline(line, span.StartLine, pos, output);
    }

    /// <summary>
    /// 对单行从 startCol 起扫描行内语法，与 MarkdownParser.ParseInline 顺序一致，输出精确 (line, col, len, kind)。
    /// </summary>
    private static void ScanLineInline(string line, int lineIndex, int startCol, List<HighlightToken> output)
    {
        int pos = startCol;
        while (pos < line.Length)
        {
            int len = 0;
            HighlightKind? kind = null;

            // ![alt](url)
            if (pos + 2 < line.Length && line[pos] == '!' && line[pos + 1] == '[')
            {
                int endAlt = pos + 2;
                while (endAlt < line.Length && line[endAlt] != ']') endAlt++;
                if (endAlt < line.Length && endAlt + 2 < line.Length && line[endAlt + 1] == '(')
                {
                    int endUrl = endAlt + 2;
                    while (endUrl < line.Length && line[endUrl] != ')' && line[endUrl] != ' ') endUrl++;
                    if (endUrl < line.Length && line[endUrl] == ')')
                    {
                        len = endUrl - pos + 1;
                        kind = HighlightKind.Image;
                    }
                }
            }

            // [^id]
            if (kind == null && pos + 3 <= line.Length && line[pos] == '[' && line[pos + 1] == '^')
            {
                int end = pos + 2;
                while (end < line.Length && line[end] != ']') end++;
                if (end < line.Length)
                {
                    len = end - pos + 1;
                    kind = HighlightKind.FootnoteRef;
                }
            }

            // [text](url)
            if (kind == null && pos + 1 < line.Length && line[pos] == '[')
            {
                int endText = pos + 1;
                while (endText < line.Length && line[endText] != ']') endText++;
                if (endText < line.Length && endText + 2 < line.Length && line[endText + 1] == '(')
                {
                    int endUrl = endText + 2;
                    while (endUrl < line.Length && line[endUrl] != ')' && line[endUrl] != ' ') endUrl++;
                    if (endUrl < line.Length && line[endUrl] == ')')
                    {
                        len = endUrl - pos + 1;
                        kind = HighlightKind.Link;
                    }
                }
            }

            // $...$ 行内公式（非 $$）
            if (kind == null && pos < line.Length && line[pos] == '$' && (pos + 1 >= line.Length || line[pos + 1] != '$'))
            {
                int end = pos + 1;
                while (end < line.Length && line[end] != '\n')
                {
                    if (line[end] == '$' && (end + 1 >= line.Length || line[end + 1] != '$'))
                    {
                        len = end - pos + 1;
                        kind = HighlightKind.MathInline;
                        break;
                    }
                    end++;
                }
            }

            // `...`
            if (kind == null && pos < line.Length && line[pos] == '`')
            {
                int end = pos + 1;
                while (end < line.Length && line[end] != '`') end++;
                if (end < line.Length)
                {
                    len = end - pos + 1;
                    kind = HighlightKind.CodeInline;
                }
            }

            // **...**
            if (kind == null && pos + 4 <= line.Length && line[pos] == '*' && line[pos + 1] == '*')
            {
                int inner = pos + 2;
                while (inner + 2 <= line.Length)
                {
                    if (line[inner] == '*' && line[inner + 1] == '*')
                    {
                        len = inner + 2 - pos;
                        kind = HighlightKind.Strong;
                        break;
                    }
                    inner++;
                }
            }

            // __...__
            if (kind == null && pos + 4 <= line.Length && line[pos] == '_' && line[pos + 1] == '_')
            {
                int inner = pos + 2;
                while (inner + 2 <= line.Length)
                {
                    if (line[inner] == '_' && line[inner + 1] == '_')
                    {
                        len = inner + 2 - pos;
                        kind = HighlightKind.Strong;
                        break;
                    }
                    inner++;
                }
            }

            // ~~...~~
            if (kind == null && pos + 4 <= line.Length && line[pos] == '~' && line[pos + 1] == '~')
            {
                int inner = pos + 2;
                while (inner + 2 <= line.Length)
                {
                    if (line[inner] == '~' && line[inner + 1] == '~')
                    {
                        len = inner + 2 - pos;
                        kind = HighlightKind.Strikethrough;
                        break;
                    }
                    inner++;
                }
            }

            // *...* 斜体（单字符，且不能是 **）
            if (kind == null && pos + 2 < line.Length && line[pos] == '*' && line[pos + 1] != '*')
            {
                int inner = pos + 1;
                while (inner < line.Length)
                {
                    if (line[inner] == '\n') break;
                    if (line[inner] == '*')
                    {
                        len = inner - pos + 1;
                        kind = HighlightKind.Emphasis;
                        break;
                    }
                    inner++;
                }
            }

            // _..._ 斜体（单字符，且不能是 __）
            if (kind == null && pos + 2 < line.Length && line[pos] == '_' && line[pos + 1] != '_')
            {
                int inner = pos + 1;
                while (inner < line.Length)
                {
                    if (line[inner] == '\n') break;
                    if (line[inner] == '_')
                    {
                        len = inner - pos + 1;
                        kind = HighlightKind.Emphasis;
                        break;
                    }
                    inner++;
                }
            }

            if (kind != null && len > 0)
            {
                output.Add(new HighlightToken(lineIndex, pos, len, kind.Value));
                pos += len;
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// 针对一个块范围，再做行内级别的语法 token （粗体/斜体/链接/行内代码/行内数学等）。
    /// 这里为了简单，直接对该块的文本整体调用 MarkdownParser.Parse，然后按行重新拆分。
    /// </summary>
    private static void AddInlineTokens(IDocumentSource doc, BlockSpan span, List<HighlightToken> output)
    {
        // 目前暂时关闭行内高亮，避免与 MarkdownParser 的内容索引偏移导致颜色错位。
        // 如需重新启用，可在此处基于 AST 重新计算精确列范围。
        return;

        // 拼块文本
        var sb = new System.Text.StringBuilder();
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (i > span.StartLine)
                sb.Append('\n');
            sb.Append(doc.GetLine(i).ToString());
        }
        var text = sb.ToString();
        if (text.Length == 0)
            return;

        var ast = MarkdownParser.Parse(text);
        var lineOffsets = ComputeLineOffsets(text);

        foreach (var node in ast.Children)
        {
            if (node is ParagraphNode p)
            {
                AddInlineFromList(p.Content, span.StartLine, lineOffsets, output);
            }
            else if (node is HeadingNode h)
            {
                AddInlineFromList(h.Content, span.StartLine, lineOffsets, output);
            }
            else if (node is BulletListNode bl)
            {
                foreach (var item in bl.Items)
                {
                    foreach (var c in item.Content)
                    {
                        if (c is ParagraphNode pp)
                            AddInlineFromList(pp.Content, span.StartLine, lineOffsets, output);
                    }
                }
            }
            else if (node is OrderedListNode ol)
            {
                foreach (var item in ol.Items)
                {
                    foreach (var c in item.Content)
                    {
                        if (c is ParagraphNode pp)
                            AddInlineFromList(pp.Content, span.StartLine, lineOffsets, output);
                    }
                }
            }
        }
    }

    private static int[] ComputeLineOffsets(string text)
    {
        var list = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                list.Add(i + 1);
        }
        return list.ToArray();
    }

    private static (int line, int column) GetLineColumn(int[] lineOffsets, int absoluteIndex)
    {
        int line = Array.BinarySearch(lineOffsets, absoluteIndex);
        if (line < 0)
        {
            line = ~line - 1;
            if (line < 0)
                line = 0;
        }
        int column = absoluteIndex - lineOffsets[line];
        return (line, column);
    }

    private static void AddInlineFromList(
        List<InlineNode> inlines,
        int baseLine,
        int[] lineOffsets,
        List<HighlightToken> output)
    {
        int cursor = 0;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextNode t:
                    cursor += t.Content.Length;
                    break;

                case CodeNode c:
                    AddSpanTokens(c.Content.Length, HighlightKind.CodeInline);
                    break;

                case LinkNode l:
                    AddSpanTokens(l.Text.Length, HighlightKind.Link);
                    break;

                case BoldNode b:
                    AddNested(b.Content, HighlightKind.Strong);
                    break;

                case ItalicNode it:
                    AddNested(it.Content, HighlightKind.Emphasis);
                    break;

                case StrikethroughNode s:
                    AddNested(s.Content, HighlightKind.Strikethrough);
                    break;

                case MathInlineNode m:
                    AddSpanTokens(m.LaTeX.Length, HighlightKind.MathInline);
                    break;

                default:
                    break;
            }
        }

        void AddSpanTokens(int length, HighlightKind kind)
        {
            if (length <= 0)
                return;
            var (line, col) = GetLineColumn(lineOffsets, cursor);
            output.Add(new HighlightToken(baseLine + line, col, length, kind));
            cursor += length;
        }

        void AddNested(List<InlineNode> nested, HighlightKind kind)
        {
            foreach (var node in nested)
            {
                if (node is TextNode t2)
                {
                    if (t2.Content.Length > 0)
                    {
                        var (line, col) = GetLineColumn(lineOffsets, cursor);
                        output.Add(new HighlightToken(baseLine + line, col, t2.Content.Length, kind));
                        cursor += t2.Content.Length;
                    }
                }
                else
                {
                    // 其他行内节点按各自规则处理
                    switch (node)
                    {
                        case CodeNode c2:
                            AddSpanTokens(c2.Content.Length, HighlightKind.CodeInline);
                            break;
                        case LinkNode l2:
                            AddSpanTokens(l2.Text.Length, HighlightKind.Link);
                            break;
                        case MathInlineNode m2:
                            AddSpanTokens(m2.LaTeX.Length, HighlightKind.MathInline);
                            break;
                        default:
                            break;
                    }
                }
            }
        }
    }
}

