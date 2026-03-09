using System.Runtime.CompilerServices;
using System.Text;

namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 可变字符串文档源：在不更换实例的前提下更新全文本内容，支持按行随机访问。
/// 适合与渲染引擎长生命周期配合使用，避免频繁分配新的 IDocumentSource。
/// </summary>
public sealed class MutableStringDocumentSource : IDocumentSource, IVersionedDocumentSource
{
    private string _text;
    private int[]? _lineStarts;
    private int _lineCount = -1;

    public MutableStringDocumentSource(string text)
    {
        _text = text ?? string.Empty;
    }

    /// <summary>当前内容版本号，每次 SetText 时自增，用于通知 RenderEngine 文档内容已变更。</summary>
    public int Version { get; private set; }

    public ReadOnlySpan<char> FullText => _text.AsSpan();

    public bool SupportsRandomAccess => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetLine(int index)
    {
        EnsureLineIndex();
        if (index < 0 || index >= _lineCount) return default;
        int start = _lineStarts![index];
        int end = index + 1 < _lineCount ? _lineStarts[index + 1] : _text.Length;
        if (end > start && _text[end - 1] == '\n') end--;
        if (end > start && _text[end - 1] == '\r') end--;
        return _text.AsSpan(start, end - start);
    }

    public ReadOnlySpan<char> GetLines(int start, int count)
    {
        EnsureLineIndex();
        if (start < 0 || count <= 0 || start >= _lineCount) return default;
        int endLine = Math.Min(start + count, _lineCount);
        int startPos = _lineStarts![start];
        int endPos = endLine < _lineCount ? _lineStarts[endLine] : _text.Length;
        return _text.AsSpan(startPos, endPos - startPos);
    }

    public int LineCount
    {
        get
        {
            EnsureLineIndex();
            return _lineCount;
        }
    }

    /// <summary>更新全文本内容，并使行索引和版本号失效，以便渲染引擎在下一次访问时重新解析。</summary>
    public void SetText(string text)
    {
        _text = text ?? string.Empty;
        _lineStarts = null;
        _lineCount = -1;
        unchecked
        {
            Version++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureLineIndex()
    {
        if (_lineStarts != null) return;
        var list = new List<int>(Math.Max(256, _text.Length / 40)) { 0 };
        for (int i = 0; i < _text.Length; i++)
        {
            if (_text[i] == '\n')
                list.Add(i + 1);
        }
        _lineStarts = list.ToArray();
        _lineCount = list.Count;
    }
}

