using Avalonia.Controls;

namespace MarkdownEditor.Views;

public enum ConfirmCloseResult { Cancel, Save, Discard }

public partial class ConfirmCloseWindow : Window
{
    public ConfirmCloseResult Result { get; private set; } = ConfirmCloseResult.Cancel;

    public ConfirmCloseWindow()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => { Result = ConfirmCloseResult.Save; Close(); };
        DiscardButton.Click += (_, _) => { Result = ConfirmCloseResult.Discard; Close(); };
        CancelButton.Click += (_, _) => { Result = ConfirmCloseResult.Cancel; Close(); };
    }
}
