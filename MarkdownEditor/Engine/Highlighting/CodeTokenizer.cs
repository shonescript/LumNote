using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkdownEditor.Engine.Highlighting;

/// <summary>
/// 代码块内按行的轻量级 token 种类（用于语法高亮）。
/// </summary>
public enum CodeTokenKind
{
    Default,
    Keyword,
    String,
    Comment,
    Number,
}

/// <summary>
/// 轻量级代码行 tokenizer：按语言用正则+关键字切分，供预览代码块语法高亮使用。
/// </summary>
public static class CodeTokenizer
{
    public readonly struct TokenSpan
    {
        public int Start { get; }
        public int Length { get; }
        public CodeTokenKind Kind { get; }
        public TokenSpan(int start, int length, CodeTokenKind kind)
        {
            Start = start;
            Length = length;
            Kind = kind;
        }
    }

    /// <summary>
    /// 对单行代码按给定语言做 token 切分。不支持的语言返回整行一个 Default。
    /// </summary>
    public static IReadOnlyList<TokenSpan> TokenizeLine(string language, string line)
    {
        if (string.IsNullOrEmpty(line)) return [];
        var lang = (language ?? "").Trim().ToLowerInvariant();
        return lang switch
        {
            "c#" or "csharp" or "cs" => TokenizeCSharp(line),
            "js" or "javascript" => TokenizeJavaScript(line),
            "py" or "python" => TokenizePython(line),
            "json" => TokenizeJson(line),
            "html" or "xml" => TokenizeHtmlOrXml(line),
            "css" => TokenizeCss(line),
            "sql" => TokenizeSql(line),
            _ => TokenizeGeneric(line)
        };
    }

    private static IReadOnlyList<TokenSpan> TokenizeCSharp(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double",
            "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float",
            "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public", "readonly", "ref",
            "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while"
        };
        TokenizeWithRegex(line, list, keywords,
            blockComment: (@"/\*", @"\*/"),
            lineComment: "//",
            strings: [@"@?""[^""]*""", @"@?\'[^']*\'"],
            numberPattern: @"\b\d+\.?\d*([eE][+-]?\d+)?[fFdDmMlL]?\b");
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizeJavaScript(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "await", "break", "case", "catch", "class", "const", "continue", "debugger",
            "default", "delete", "do", "else", "export", "extends", "false", "finally",
            "for", "function", "if", "import", "in", "instanceof", "let", "new", "null",
            "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var",
            "void", "while", "yield"
        };
        TokenizeWithRegex(line, list, keywords,
            blockComment: (@"/\*", @"\*/"),
            lineComment: "//",
            strings: [@"""(?:[^""\\]|\\.)*""", @"'(?:[^'\\]|\\.)*'", @"`(?:[^`\\]|\\.)*`"],
            numberPattern: @"\b\d+\.?\d*([eE][+-]?\d+)?\b");
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizePython(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break",
            "class", "continue", "def", "del", "elif", "else", "except", "finally",
            "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
            "not", "or", "pass", "raise", "return", "try", "while", "with", "yield"
        };
        TokenizeWithRegex(line, list, keywords,
            blockComment: null,
            lineComment: "#",
            strings: [@"""(?:[^""\\]|\\.)*""", @"'(?:[^'\\]|\\.)*'", @"'''(?:[^\\]|\\.)*'''", @"""""(?:[^\\]|\\.)*"""""],
            numberPattern: @"\b\d+\.?\d*([eE][+-]?\d+)?[jJ]?\b");
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizeJson(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" };
        TokenizeWithRegex(line, list, keywords,
            blockComment: null,
            lineComment: null,
            strings: [@"""(?:[^""\\]|\\.)*"""],
            numberPattern: @"\b-?\d+\.?\d*([eE][+-]?\d+)?\b");
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizeHtmlOrXml(string line)
    {
        var list = new List<TokenSpan>();
        // 注释 <!-- ... -->
        var commentMatch = Regex.Match(line, @"<!--.*?-->");
        if (commentMatch.Success)
        {
            AddNonOverlapping(line, list, 0, commentMatch.Index, CodeTokenKind.Default);
            list.Add(new TokenSpan(commentMatch.Index, commentMatch.Length, CodeTokenKind.Comment));
            AddNonOverlapping(line, list, commentMatch.Index + commentMatch.Length, line.Length, CodeTokenKind.Default);
            return list;
        }
        // 标签 <...> 可视为 Default，字符串 "..." 和 '...'
        TokenizeWithRegex(line, list, new HashSet<string>(StringComparer.Ordinal),
            blockComment: null,
            lineComment: null,
            strings: [@"""[^""]*""", @"'[^']*'"],
            numberPattern: null);
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizeCss(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "align", "auto", "block", "bold", "border", "both", "bottom", "center",
            "color", "display", "flex", "float", "grid", "hidden", "inline", "left",
            "margin", "none", "normal", "padding", "relative", "right", "solid", "static",
            "top", "underline", "visible", "white", "wrap"
        };
        TokenizeWithRegex(line, list, keywords,
            blockComment: (@"/\*", @"\*/"),
            lineComment: null,
            strings: [@"""[^""]*""", @"'[^']*'"],
            numberPattern: @"\b\d+\.?\d*(px|em|rem|%|deg)?\b");
        return list;
    }

    private static IReadOnlyList<TokenSpan> TokenizeSql(string line)
    {
        var list = new List<TokenSpan>();
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "select", "from", "where", "insert", "into", "values", "update", "set",
            "delete", "create", "table", "drop", "index", "view", "join", "left", "right",
            "inner", "outer", "on", "and", "or", "not", "null", "order", "by", "group",
            "having", "as", "asc", "desc", "limit", "offset", "union", "all", "distinct"
        };
        TokenizeWithRegex(line, list, keywords,
            blockComment: (@"/\*", @"\*/"),
            lineComment: "--",
            strings: [@"""(?:[^""]|"""")*""", @"'(?:[^']|'')*'"],
            numberPattern: @"\b\d+\.?\d*\b");
        return list;
    }

    /// <summary>
    /// 语言未知时的通用 tokenizer：高亮字符串、数字和常见注释（# / //），
    /// 以便在未声明语言的代码块中也能获得基础颜色。
    /// </summary>
    private static IReadOnlyList<TokenSpan> TokenizeGeneric(string line)
    {
        var list = new List<TokenSpan>();
        if (string.IsNullOrEmpty(line))
            return list;

        // 以 # 开头的一整行视为注释（适配 shell/python）
        var trimmed = line.TrimStart();
        int leading = line.Length - trimmed.Length;
        if (trimmed.StartsWith("#"))
        {
            list.Add(new TokenSpan(leading, line.Length - leading, CodeTokenKind.Comment));
            return list;
        }

        // 其他情况复用 C 风格的通用规则：// 注释、数字、字符串
        TokenizeWithRegex(
            line,
            list,
            new HashSet<string>(StringComparer.Ordinal),
            blockComment: (@"/\*", @"\*/"),
            lineComment: "//",
            strings: [@"""(?:[^""\\]|\\.)*""", @"'(?:[^'\\]|\\.)*'"],
            numberPattern: @"\b\d+\.?\d*([eE][+-]?\d+)?\b");

        return list;
    }

    private static void AddNonOverlapping(string line, List<TokenSpan> list, int start, int end, CodeTokenKind kind)
    {
        if (end <= start) return;
        list.Add(new TokenSpan(start, end - start, kind));
    }

    private static void TokenizeWithRegex(
        string line,
        List<TokenSpan> list,
        HashSet<string> keywords,
        (string start, string end)? blockComment,
        string? lineComment,
        string[]? strings,
        string? numberPattern)
    {
        int pos = 0;
        while (pos < line.Length)
        {
            int next = line.Length;
            CodeTokenKind kind = CodeTokenKind.Default;

            // 块注释
            if (blockComment != null)
            {
                var bcStart = Regex.Match(line[pos..], Regex.Escape(blockComment.Value.start));
                if (bcStart.Success && bcStart.Index == 0)
                {
                    var bcEnd = Regex.Match(line[(pos + bcStart.Length)..], Regex.Escape(blockComment.Value.end));
                    if (bcEnd.Success)
                    {
                        int endPos = pos + bcStart.Length + bcEnd.Index + bcEnd.Length;
                        list.Add(new TokenSpan(pos, endPos - pos, CodeTokenKind.Comment));
                        pos = endPos;
                        continue;
                    }
                }
            }

            // 行注释
            if (lineComment != null)
            {
                int lcIdx = line.IndexOf(lineComment, pos, StringComparison.Ordinal);
                if (lcIdx == pos)
                {
                    list.Add(new TokenSpan(pos, line.Length - pos, CodeTokenKind.Comment));
                    return;
                }
                if (lcIdx >= 0 && lcIdx < next) next = lcIdx;
            }

            // 字符串（优先）
            if (strings != null)
            {
                foreach (var pat in strings)
                {
                    var m = Regex.Match(line[pos..], pat);
                    if (m.Success && m.Index == 0 && pos + m.Length < next)
                    {
                        next = pos + m.Length;
                        kind = CodeTokenKind.String;
                        break;
                    }
                }
            }

            // 数字
            if (numberPattern != null && kind == CodeTokenKind.Default)
            {
                var num = Regex.Match(line[pos..], numberPattern);
                if (num.Success && num.Index == 0 && pos + num.Length <= next)
                {
                    next = pos + num.Length;
                    kind = CodeTokenKind.Number;
                }
            }

            // 关键字（整词）
            if (kind == CodeTokenKind.Default && keywords.Count > 0)
            {
                var word = Regex.Match(line[pos..], @"\b([a-zA-Z_][a-zA-Z0-9_]*)\b");
                if (word.Success && word.Index == 0 && keywords.Contains(word.Groups[1].Value) && pos + word.Length <= next)
                {
                    next = pos + word.Length;
                    kind = CodeTokenKind.Keyword;
                }
            }

            if (next > pos)
            {
                list.Add(new TokenSpan(pos, next - pos, kind));
                pos = next;
            }
            else
            {
                // 单字符默认
                list.Add(new TokenSpan(pos, 1, CodeTokenKind.Default));
                pos++;
            }
        }
    }
}
