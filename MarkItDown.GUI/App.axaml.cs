using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MarkItDown.GUI;

/// <summary>
/// アプリケーションのメインクラス
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;

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
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            
            desktop.ShutdownRequested += OnShutdownRequested;
            
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// アプリケーション終了時のハンドラー
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        CleanupResources();
    }

    /// <summary>
    /// プロセス終了時のハンドラー（強制終了時も呼ばれる）
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        CleanupResources();
    }

    /// <summary>
    /// リソースをクリーンアップする
    /// </summary>
    private void CleanupResources()
    {
        try
        {
            if (_mainWindow?.DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // 終了時のエラーは無視
        }
    }
}
