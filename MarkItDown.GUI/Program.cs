using System;
using Avalonia;
using MarkItDown.GUI.Services;
using Velopack;

namespace MarkItDown.GUI;

/// <summary>
/// アプリケーションのエントリポイント
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
