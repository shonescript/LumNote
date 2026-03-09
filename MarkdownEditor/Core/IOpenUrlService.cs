namespace MarkdownEditor.Core;

/// <summary>打开 URL（如链接、本地文件）的抽象，便于跨平台实现与测试。</summary>
public interface IOpenUrlService
{
    void Open(string url);
}
