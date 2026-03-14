using LumConfig;

namespace MarkdownEditor.Core;

/// <summary>
/// 应用配置 - 界面与 Markdown 风格（使用 LumConfig 持久化，AOT 友好）
/// </summary>
public sealed class AppConfig
{
    public UiConfig Ui { get; set; } = new();
    public MarkdownStyleConfig Markdown { get; set; } = new();

    public static AppConfig Load(string path)
    {
        var result = new AppConfig();
        try
        {
            if (!File.Exists(path))
                return result;
            var cfg = new LumConfigManager(path);

            // Ui
            result.Ui.DocumentListWidth = cfg.GetDouble("ui:documentListWidth") ?? result.Ui.DocumentListWidth;
            result.Ui.EditorWidth = cfg.GetDouble("ui:editorWidth") ?? result.Ui.EditorWidth;
            // 不再恢复窗口尺寸和状态，避免最大化/还原时尺寸异常
            result.Ui.LayoutMode = cfg.GetString("ui:layoutMode") ?? result.Ui.LayoutMode;
            result.Ui.BackgroundColor = cfg.GetString("ui:backgroundColor") ?? result.Ui.BackgroundColor;
            result.Ui.SidebarBackground = cfg.GetString("ui:sidebarBackground") ?? result.Ui.SidebarBackground;
            result.Ui.HeaderBackground = cfg.GetString("ui:headerBackground") ?? result.Ui.HeaderBackground;
            result.Ui.LinkColor = cfg.GetString("ui:linkColor") ?? result.Ui.LinkColor;

            // Markdown
            result.Markdown.CodeBlockBackground = cfg.GetString("markdown:codeBlockBackground") ?? result.Markdown.CodeBlockBackground;
            result.Markdown.CodeBlockTextColor = cfg.GetString("markdown:codeBlockTextColor") ?? result.Markdown.CodeBlockTextColor;
            result.Markdown.CodeKeywordColor = cfg.GetString("markdown:codeKeywordColor") ?? result.Markdown.CodeKeywordColor;
            result.Markdown.CodeStringColor = cfg.GetString("markdown:codeStringColor") ?? result.Markdown.CodeStringColor;
            result.Markdown.CodeCommentColor = cfg.GetString("markdown:codeCommentColor") ?? result.Markdown.CodeCommentColor;
            result.Markdown.CodeNumberColor = cfg.GetString("markdown:codeNumberColor") ?? result.Markdown.CodeNumberColor;
            result.Markdown.CodeDefaultColor = cfg.GetString("markdown:codeDefaultColor") ?? result.Markdown.CodeDefaultColor;
            result.Markdown.BlockquoteBorderColor = cfg.GetString("markdown:blockquoteBorderColor") ?? result.Markdown.BlockquoteBorderColor;
            result.Markdown.TableBorderColor = cfg.GetString("markdown:tableBorderColor") ?? result.Markdown.TableBorderColor;
            result.Markdown.TableHeaderBackground = cfg.GetString("markdown:tableHeaderBackground") ?? result.Markdown.TableHeaderBackground;
            result.Markdown.Heading1Size = cfg.GetInt("markdown:heading1Size") ?? result.Markdown.Heading1Size;
            result.Markdown.Heading2Size = cfg.GetInt("markdown:heading2Size") ?? result.Markdown.Heading2Size;
            result.Markdown.CodeFontFamily = cfg.GetString("markdown:codeFontFamily") ?? result.Markdown.CodeFontFamily;
            result.Markdown.BodyFontFamily = cfg.GetString("markdown:bodyFontFamily") ?? result.Markdown.BodyFontFamily;
            result.Markdown.LinkColor = cfg.GetString("markdown:linkColor") ?? result.Markdown.LinkColor;
            result.Markdown.TextColor = cfg.GetString("markdown:textColor") ?? result.Markdown.TextColor;
            result.Markdown.BackgroundColor = cfg.GetString("markdown:backgroundColor") ?? result.Markdown.BackgroundColor;
            result.Markdown.MathBackground = cfg.GetString("markdown:mathBackground") ?? result.Markdown.MathBackground;
            result.Markdown.SelectionColor = cfg.GetString("markdown:selectionColor") ?? result.Markdown.SelectionColor;
            result.Markdown.ImagePlaceholderColor = cfg.GetString("markdown:imagePlaceholderColor") ?? result.Markdown.ImagePlaceholderColor;
            result.Markdown.ZoomLevel = cfg.GetDouble("markdown:zoomLevel") ?? result.Markdown.ZoomLevel;
        }
        catch { }
        return result;
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cfg = new LumConfigManager();

            // Ui
            cfg.Set("ui:documentListWidth", Ui.DocumentListWidth);
            cfg.Set("ui:editorWidth", Ui.EditorWidth);
            // 不再持久化窗口尺寸和状态，统一交由系统窗口管理负责
            cfg.Set("ui:layoutMode", Ui.LayoutMode);
            cfg.Set("ui:backgroundColor", Ui.BackgroundColor);
            cfg.Set("ui:sidebarBackground", Ui.SidebarBackground);
            cfg.Set("ui:headerBackground", Ui.HeaderBackground);
            cfg.Set("ui:linkColor", Ui.LinkColor);

            // Markdown
            cfg.Set("markdown:codeBlockBackground", Markdown.CodeBlockBackground);
            cfg.Set("markdown:codeBlockTextColor", Markdown.CodeBlockTextColor);
            cfg.Set("markdown:codeKeywordColor", Markdown.CodeKeywordColor);
            cfg.Set("markdown:codeStringColor", Markdown.CodeStringColor);
            cfg.Set("markdown:codeCommentColor", Markdown.CodeCommentColor);
            cfg.Set("markdown:codeNumberColor", Markdown.CodeNumberColor);
            cfg.Set("markdown:codeDefaultColor", Markdown.CodeDefaultColor);
            cfg.Set("markdown:blockquoteBorderColor", Markdown.BlockquoteBorderColor);
            cfg.Set("markdown:tableBorderColor", Markdown.TableBorderColor);
            cfg.Set("markdown:tableHeaderBackground", Markdown.TableHeaderBackground);
            cfg.Set("markdown:heading1Size", Markdown.Heading1Size);
            cfg.Set("markdown:heading2Size", Markdown.Heading2Size);
            cfg.Set("markdown:codeFontFamily", Markdown.CodeFontFamily);
            cfg.Set("markdown:bodyFontFamily", Markdown.BodyFontFamily);
            cfg.Set("markdown:linkColor", Markdown.LinkColor);
            cfg.Set("markdown:textColor", Markdown.TextColor);
            cfg.Set("markdown:backgroundColor", Markdown.BackgroundColor);
            cfg.Set("markdown:mathBackground", Markdown.MathBackground);
            cfg.Set("markdown:selectionColor", Markdown.SelectionColor);
            cfg.Set("markdown:imagePlaceholderColor", Markdown.ImagePlaceholderColor);
            cfg.Set("markdown:zoomLevel", Markdown.ZoomLevel);

            cfg.Save(path);
        }
        catch { }
    }

    private static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>配置文件路径：与可执行文件同目录，方便便携分发。</summary>
    public static string DefaultConfigPath => Path.Combine(BaseDirectory, "config.json");

    /// <summary>最近打开文件列表持久化路径（与 config 同目录）。</summary>
    public static string RecentFilesPath => Path.Combine(BaseDirectory, "recent-files.json");

    /// <summary>最近打开文件夹列表持久化路径。</summary>
    public static string RecentFoldersPath => Path.Combine(BaseDirectory, "recent-folders.json");
}

public sealed class UiConfig
{
    public double DocumentListWidth { get; set; } = 280;
    public double EditorWidth { get; set; } = 1;
    // 窗口尺寸与状态不再持久化，交由系统默认行为管理
    /// <summary>编辑/预览布局模式（Both / EditorOnly / PreviewOnly）。</summary>
    public string LayoutMode { get; set; } = "Both";
    public string BackgroundColor { get; set; } = "#f6f8fa";
    public string SidebarBackground { get; set; } = "#ffffff";
    public string HeaderBackground { get; set; } = "#24292f";
    public string LinkColor { get; set; } = "#0566d2";
}

public sealed class MarkdownStyleConfig
{
    public string CodeBlockBackground { get; set; } = "#252526";
    public string CodeBlockTextColor { get; set; } = "#dcdcdc";
    public string CodeKeywordColor { get; set; } = "#569cd6";
    public string CodeStringColor { get; set; } = "#ce9178";
    public string CodeCommentColor { get; set; } = "#6a9955";
    public string CodeNumberColor { get; set; } = "#b5cea8";
    public string CodeDefaultColor { get; set; } = "#d4d4d4";
    public string BlockquoteBorderColor { get; set; } = "#3f3f46";
    public string TableBorderColor { get; set; } = "#3f3f46";
    public string TableHeaderBackground { get; set; } = "#2a2d2e";
    public int Heading1Size { get; set; } = 28;
    public int Heading2Size { get; set; } = 24;
    public string CodeFontFamily { get; set; } = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace";
    public string BodyFontFamily { get; set; } = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif";
    public string LinkColor { get; set; } = "#3794ff";
    public string TextColor { get; set; } = "#d4d4d4";
    public string BackgroundColor { get; set; } = "#1e1e1e";
    public string MathBackground { get; set; } = "#252526";
    public string SelectionColor { get; set; } = "#503399ff";
    public string ImagePlaceholderColor { get; set; } = "#404040";
    public double ZoomLevel { get; set; } = 1.0;
}
