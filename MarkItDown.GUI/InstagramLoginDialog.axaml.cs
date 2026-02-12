using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MarkItDown.GUI;

/// <summary>
/// Instagram ログインダイアログ。
/// ユーザー名とパスワードを入力させて (string, string)? として返す。
/// </summary>
public partial class InstagramLoginDialog : Window
{
    public InstagramLoginDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UsernameInput.Focus(),
                Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// ダイアログのメッセージを設定する
    /// </summary>
    public void SetMessage(string message)
    {
        MessageText.Text = message;
    }

    private void LoginButton_Click(object? sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text?.Trim();
        var password = PasswordInput.Text;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return; // 入力が不足している場合は何もしない
        }

        Close((username, password));
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(((string, string)?)null);
    }
}
