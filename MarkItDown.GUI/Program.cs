using System;
using System.Threading;
using Avalonia;
using MarkItDown.GUI.Services;
using Velopack;

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

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

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
        var updateFeedUrl = AppSettings.GetUpdateFeedUrl();

        try
        {
            var updateManager = new UpdateManager(updateFeedUrl);
            var updateInfo = updateManager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (updateInfo is not null)
            {
                Logger.Log("アップデートが見つかりました。適用中...", LogLevel.Info);
                updateManager.DownloadUpdatesAsync(updateInfo).GetAwaiter().GetResult();
                updateManager.ApplyUpdatesAndRestart(updateInfo);
                return 0;
            }
        }
        catch (Velopack.Exceptions.NotInstalledException)
        {
            Logger.Log("開発環境のため更新チェックをスキップしました", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.LogException("更新チェック中にエラーが発生しました", ex);
        }

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
    /// Avalonia アプリケーションをビルドする
    /// </summary>
    /// <returns>AppBuilder インスタンス</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
