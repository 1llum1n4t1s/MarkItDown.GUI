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
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    /// <summary>二重解放防止フラグ</summary>
    private bool _resourcesCleanedUp;

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
            _desktop = desktop;
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
        // ShutdownRequested と重複して呼ばれた場合でも安全に処理する
        CleanupResources();

        // ProcessExit ハンドラーは登録解除（GC ルートからの参照を切る）
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        if (_desktop is not null)
        {
            _desktop.ShutdownRequested -= OnShutdownRequested;
        }
    }

    /// <summary>
    /// リソースをクリーンアップする（二重呼び出し安全）
    /// </summary>
    private void CleanupResources()
    {
        if (_resourcesCleanedUp)
        {
            return;
        }
        _resourcesCleanedUp = true;

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
