namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 可选的版本化文档源扩展接口：用于在不更换 IDocumentSource 实例的前提下，
/// 向渲染引擎显式传递“内容已变更”的信号。
/// </summary>
public interface IVersionedDocumentSource : IDocumentSource
{
    /// <summary>当前内容版本号；内容发生变更时应递增。</summary>
    int Version { get; }
}

