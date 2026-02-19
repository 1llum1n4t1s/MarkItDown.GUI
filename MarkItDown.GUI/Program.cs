using System;
using System.Threading;
using Avalonia;
using MarkItDown.GUI.Services;
using Velopack;
using Velopack.Sources;

namespace MarkItDown.GUI;

/// <summary>
/// アプリケーションのエントリポイント
/// </summary>
internal static class Program
{
    /// <summary>
    /// 多重起動防止用 Mutex 名
    /// </summary>
    private const string MutexName = "Global\\MarkItDown.GUI.SingleInstance";

    /// <summary>
    /// 更新チェックのタイムアウト時間（ミリ秒）
    /// </summary>
    private const int UpdateCheckTimeoutMs = 10000;

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(v => StartupRegistration.Register())
            .OnAfterUpdateFastCallback(v => StartupRegistration.Register())
            .OnBeforeUninstallFastCallback(v => StartupRegistration.Unregister())
            .Run();

        // サイレント更新チェックモード
        if (args.Length > 0 && args[0] == "--update-check")
        {
            RunSilentUpdateCheck();
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 既にアプリケーションが起動している
            return 0;
        }

        Logger.Initialize();
        Logger.LogStartup(args);
        Logger.Log("アプリケーション起動", LogLevel.Info);

        AppSettings.LoadSettings();

        try
        {
            var result = BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);

            Logger.Log("アプリケーション正常終了", LogLevel.Info);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogException("アプリケーション実行中に致命的なエラーが発生しました", ex);
            return 1;
        }
        finally
        {
            Logger.Dispose();
        }
    }

    /// <summary>
    /// UI なしでサイレント更新チェックを実行する。
    /// Windows ログイン時のスタートアップから呼び出される。
    /// </summary>
    private static void RunSilentUpdateCheck()
    {
        try
        {
            Logger.Initialize();
            AppSettings.LoadSettings();

            Logger.Log("サイレント更新チェックを開始します。", LogLevel.Info);

            var repoOwner = AppSettings.GetUpdateRepoOwner();
            var repoName = AppSettings.GetUpdateRepoName();
            var channel = AppSettings.GetUpdateChannel();

            if (string.IsNullOrWhiteSpace(repoOwner) || string.IsNullOrWhiteSpace(repoName))
            {
                Logger.Log("更新元リポジトリが未設定のため更新チェックをスキップします。", LogLevel.Info);
                return;
            }

            var repoUrl = $"https://github.com/{repoOwner}/{repoName}";
            var isPrerelease = channel.Equals("prerelease", StringComparison.OrdinalIgnoreCase);
            var source = new GithubSource(repoUrl, string.Empty, isPrerelease);
            var updateManager = new UpdateManager(source);

            if (!updateManager.IsInstalled)
            {
                Logger.Log("開発環境のため更新チェックをスキップしました。", LogLevel.Debug);
                return;
            }

            Logger.Log($"更新チェック: リポジトリ: {repoUrl}, チャンネル: {channel}", LogLevel.Info);

            // 更新チェック（タイムアウト付き）
            // Velopack の CheckForUpdatesAsync はキャンセルトークンを受け付けないため、
            // Task.WhenAny でタイムアウト競争を実装する
            UpdateInfo? updateInfo;
            try
            {
                var checkTask = updateManager.CheckForUpdatesAsync();
                var timeoutTask = Task.Delay(UpdateCheckTimeoutMs);
                var completed = Task.WhenAny(checkTask, timeoutTask).GetAwaiter().GetResult();
                if (completed == timeoutTask)
                {
                    Logger.Log("更新チェックがタイムアウトしました。", LogLevel.Warning);
                    return;
                }
                updateInfo = checkTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Logger.Log("更新チェックがタイムアウトしました。", LogLevel.Warning);
                return;
            }

            if (updateInfo == null)
            {
                Logger.Log("利用可能な更新はありません。", LogLevel.Info);
                return;
            }

            Logger.Log("新しいバージョンを検出しました。更新をダウンロードしています...", LogLevel.Info);

            // ダウンロード（10分タイムアウト）
            try
            {
                using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                updateManager.DownloadUpdatesAsync(updateInfo, null, downloadCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Logger.Log("ダウンロードがタイムアウトしました。", LogLevel.Warning);
                return;
            }

            Logger.Log("ダウンロード完了。更新を適用します。", LogLevel.Info);
            updateManager.ApplyUpdatesAndExit(updateInfo);
        }
        catch (Exception ex)
        {
            Logger.LogException("サイレント更新チェック中にエラーが発生しました", ex);
        }
        finally
        {
            Logger.Dispose();
        }
    }

    /// <summary>
    /// Avalonia アプリケーションをビルドする
    /// </summary>
    /// <returns>AppBuilder インスタンス</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
