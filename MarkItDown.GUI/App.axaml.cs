using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MarkItDown.GUI;

/// <summary>
/// アプリケーションのメインクラス
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// アプリケーションの初期化
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// アプリケーションのフレームワーク初期化（メインウィンドウの設定）
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
