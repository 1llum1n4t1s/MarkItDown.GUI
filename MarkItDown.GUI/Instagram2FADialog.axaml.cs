using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MarkItDown.GUI;

/// <summary>
/// Instagram 2段階認証コード入力ダイアログ。
/// コード文字列を string? として返す。
/// </summary>
public partial class Instagram2FADialog : Window
{
    public Instagram2FADialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CodeInput.Focus(),
                Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// ダイアログのメッセージを設定する
    /// </summary>
    public void SetMessage(string message)
    {
        MessageText.Text = message;
    }

    private void SubmitButton_Click(object? sender, RoutedEventArgs e)
    {
        var code = CodeInput.Text?.Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            return; // コードが空の場合は何もしない
        }

        Close(code);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close((string?)null);
    }
}
