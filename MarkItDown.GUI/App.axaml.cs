using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MarkItDown.GUI.Services;
using Velopack;
using Velopack.Sources;

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

            // 起動時の自動更新チェック（バックグラウンドで実行）
            CheckForUpdateInBackground();
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

    /// <summary>
    /// アプリ起動時にバックグラウンドで更新チェックを実行する。
    /// 更新がある場合は自動的にダウンロードして適用・再起動する。
    /// 失敗してもアプリの起動には影響しない。
    /// </summary>
    private static void CheckForUpdateInBackground()
    {
        Task.Run(async () =>
        {
            try
            {
                var repoOwner = AppSettings.GetUpdateRepoOwner();
                var repoName = AppSettings.GetUpdateRepoName();
                var channel = AppSettings.GetUpdateChannel();

                if (string.IsNullOrWhiteSpace(repoOwner) || string.IsNullOrWhiteSpace(repoName))
                {
                    Logger.Log("更新元リポジトリが未設定のため更新チェックをスキップします。", LogLevel.Debug);
                    return;
                }

                var repoUrl = $"https://github.com/{repoOwner}/{repoName}";
                var isPrerelease = channel.Equals("prerelease", StringComparison.OrdinalIgnoreCase);
                var source = new GithubSource(repoUrl, string.Empty, isPrerelease);
                var mgr = new UpdateManager(source);

                if (!mgr.IsInstalled)
                {
                    Logger.Log("開発環境のため更新チェックをスキップしました。", LogLevel.Debug);
                    return;
                }

                Logger.Log($"更新チェック開始: {repoUrl} (チャンネル: {channel})", LogLevel.Debug);

                // 更新チェック（10秒タイムアウト）
                var checkTask = mgr.CheckForUpdatesAsync();
                if (await Task.WhenAny(checkTask, Task.Delay(TimeSpan.FromSeconds(10))) != checkTask)
                {
                    Logger.Log("更新チェックがタイムアウトしました。", LogLevel.Warning);
                    return;
                }
                var updateInfo = await checkTask;

                if (updateInfo is null)
                {
                    Logger.Log("利用可能な更新はありません。", LogLevel.Debug);
                    return;
                }

                Logger.Log($"新しいバージョンを検出: {updateInfo.TargetFullRelease.Version}", LogLevel.Info);

                if (AppActivityTracker.IsBusy)
                {
                    Logger.Log("変換・スクレイピング・依存準備中のため、更新適用は次回起動へ延期します。", LogLevel.Warning);
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await mgr.DownloadUpdatesAsync(updateInfo, cancelToken: cts.Token);

                if (!AppActivityTracker.TryReserveRestart())
                {
                    Logger.Log("処理中になったため、更新の再起動適用は次回起動へ延期します。", LogLevel.Warning);
                    return;
                }

                Logger.Log("更新ダウンロード完了。再起動して適用します。", LogLevel.Info);
                try
                {
                    mgr.ApplyUpdatesAndRestart(updateInfo);
                }
                catch
                {
                    AppActivityTracker.CancelReservedRestart();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("バックグラウンド更新チェック中にエラーが発生しました", ex);
            }
        });
    }
}
