using System.IO;
using MarkdownEditor.Core;
using Xunit;

namespace MarkdownEditor.Tests.Core;

public class AppConfigTests
{
    [Fact]
    public void Load_missing_file_returns_new_config()
    {
        var path = Path.Combine(Path.GetTempPath(), "markdown-editor-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var config = AppConfig.Load(path);
            Assert.NotNull(config);
            Assert.NotNull(config.Ui);
            Assert.NotNull(config.Markdown);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_and_Load_roundtrip_preserves_data()
    {
        var path = Path.Combine(Path.GetTempPath(), "markdown-editor-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var original = new AppConfig
            {
                Ui = new UiConfig { DocumentListWidth = 300, BackgroundColor = "#111111" },
                Markdown = new MarkdownStyleConfig { TextColor = "#abcdef", ZoomLevel = 1.5 }
            };
            original.Save(path);
            Assert.True(File.Exists(path));

            var loaded = AppConfig.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(300, loaded.Ui.DocumentListWidth);
            Assert.Equal("#111111", loaded.Ui.BackgroundColor);
            Assert.Equal("#abcdef", loaded.Markdown.TextColor);
            Assert.Equal(1.5, loaded.Markdown.ZoomLevel);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
