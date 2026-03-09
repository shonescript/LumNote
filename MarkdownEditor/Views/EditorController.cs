using System;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using MarkdownEditor.Engine.Highlighting;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Views;

/// <summary>
/// 封装与编辑器相关的行为：文本绑定、语法高亮、防抖和光标状态更新。
/// 将这部分逻辑从 MainWindow 抽离，便于维护和测试。
/// </summary>
internal sealed class EditorController
{
    private readonly TextEditor _editor;
    private readonly MainViewModel _viewModel;
    private readonly MarkdownHighlightingService _highlightService;
    private readonly MarkdownColorizer _colorizer;
    private readonly DispatcherTimer _highlightTimer;
    private SearchPanel? _searchPanel;
    private int _highlightVersion;
    private int _appliedHighlightVersion;
    private string _currentEditorPath = "";
    private int _pendingGoToLine = -1;
    private bool _currentMarkdownChangeFromEditor;

    public EditorController(TextEditor editor, MainViewModel viewModel)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _highlightService = new MarkdownHighlightingService();
        _colorizer = new MarkdownColorizer(MarkdownHighlightTheme.DarkTheme);

        _highlightTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _highlightTimer.Tick += (_, _) =>
        {
            try
            {
                _highlightTimer.Stop();
                UpdateEditorHighlight();
            }
            catch
            {
                _highlightTimer.Stop();
            }
        };

        SetupEditorHighlighting();
    }

    /// <summary>请求在下一次 VM→Editor 同步时跳转到指定行（用于搜索结果导航后等待文档加载完成）。</summary>
    public void RequestGoToLine(int lineNumber)
    {
        _pendingGoToLine = lineNumber;
    }

    /// <summary>打开内置查找面板并聚焦编辑器。</summary>
    public void FocusFind()
    {
        _editor.Focus();
        _searchPanel?.Open();
    }

    private void SetupEditorHighlighting()
    {
        _searchPanel = SearchPanel.Install(_editor);

        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;

        // 启用列选择（矩形选择）能力，尽量使用公开属性，避免反射。
        var opts = _editor.Options;
        var prop = opts.GetType().GetProperty("EnableRectangularSelection");
        prop?.SetValue(opts, true);

        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);

        _editor.Text = _viewModel.CurrentMarkdown ?? string.Empty;
        _currentEditorPath = _viewModel.CurrentFilePath ?? "";

        // 初始光标信息
        _viewModel.CaretLine = _editor.TextArea.Caret.Line;
        _viewModel.CaretColumn = _editor.TextArea.Caret.Column;
        if (_viewModel.ActiveDocument != null)
            _viewModel.ActiveDocument.LastCaretOffset = _editor.TextArea.Caret.Offset;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentMarkdown))
            {
                if (_currentMarkdownChangeFromEditor)
                {
                    _currentMarkdownChangeFromEditor = false;
                    return;
                }

                var target = _viewModel.CurrentMarkdown ?? string.Empty;
                if (!string.Equals(_editor.Text, target, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(_currentEditorPath) && _currentEditorPath != (_viewModel.CurrentFilePath ?? ""))
                        _viewModel.PushBack(_currentEditorPath, _editor.TextArea.Caret.Offset);

                    var caret = _editor.TextArea.Caret.Offset;
                    _editor.Text = target;
                    _editor.TextArea.Caret.Offset = Math.Min(caret, _editor.Text.Length);
                    _currentEditorPath = _viewModel.CurrentFilePath ?? "";

                    if (_pendingGoToLine > 0 && _editor.Document != null)
                    {
                        var lineNum = Math.Clamp(_pendingGoToLine, 1, _editor.Document.LineCount);
                        var line = _editor.Document.GetLineByNumber(lineNum);
                        _editor.TextArea.Caret.Offset = line.Offset;
                        _editor.TextArea.Caret.BringCaretToView();
                        _pendingGoToLine = -1;
                    }
                }
            }
        };

        _editor.TextChanged += (_, _) =>
        {
            var text = _editor.Text ?? string.Empty;
            if (!string.Equals(_viewModel.CurrentMarkdown, text, StringComparison.Ordinal))
            {
                _currentMarkdownChangeFromEditor = true;
                _viewModel.CurrentMarkdown = text;
            }

            _highlightTimer.Stop();
            _highlightTimer.Interval = GetHighlightInterval(text);
            _highlightTimer.Start();
        };

        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _viewModel.CaretLine = _editor.TextArea.Caret.Line;
            _viewModel.CaretColumn = _editor.TextArea.Caret.Column;
            if (_viewModel.ActiveDocument != null)
                _viewModel.ActiveDocument.LastCaretOffset = _editor.TextArea.Caret.Offset;
        };
    }

    private void UpdateEditorHighlight()
    {
        var text = _editor.Text ?? string.Empty;
        var version = ++_highlightVersion;

        int? visibleStart = null;
        int? visibleEnd = null;
        var range = GetVisibleLineRange(_editor);
        if (range.HasValue)
        {
            visibleStart = range.Value.startLine;
            visibleEnd = range.Value.endLine;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var tokens = _highlightService.Analyze(text, visibleStart, visibleEnd);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (version <= _appliedHighlightVersion)
                            return;
                        if (_editor.TextArea?.TextView == null)
                            return;

                        _appliedHighlightVersion = version;
                        _colorizer.UpdateTokens(tokens);
                        _editor.TextArea.TextView.Redraw();
                    }
                    catch
                    {
                        // 控件已销毁或断开视觉树时忽略，不影响使用
                    }
                }, DispatcherPriority.Background);
            }
            catch
            {
                // 高亮分析异常不影响编辑
            }
        });
    }

    private static TimeSpan GetHighlightInterval(string text)
    {
        var length = text?.Length ?? 0;
        if (length <= 5_000)
            return TimeSpan.FromMilliseconds(180);
        if (length <= 50_000)
            return TimeSpan.FromMilliseconds(320);
        return TimeSpan.FromMilliseconds(650);
    }

    private static (int startLine, int endLine)? GetVisibleLineRange(TextEditor editor, int extraLines = 40)
    {
        var textView = editor.TextArea?.TextView;
        if (textView == null)
            return null;
        var visualLines = textView.VisualLines;
        if (visualLines == null || visualLines.Count == 0)
            return null;

        int first = visualLines.Min(v => v.FirstDocumentLine.LineNumber) - 1;
        int last = visualLines.Max(v => v.LastDocumentLine.LineNumber) - 1;

        if (editor.Document == null)
            return (Math.Max(0, first), Math.Max(first, last));

        int maxIndex = Math.Max(0, editor.Document.LineCount - 1);
        int start = Math.Max(0, first - extraLines);
        int end = Math.Min(maxIndex, last + extraLines);
        if (end < start)
            end = start;
        return (start, end);
    }
}

