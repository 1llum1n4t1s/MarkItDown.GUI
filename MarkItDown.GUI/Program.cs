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
        AppSettings.LoadSettings();
        var updateFeedUrl = AppSettings.GetUpdateFeedUrl();

        try
        {
            var updateManager = new UpdateManager(updateFeedUrl);
            var updateInfo = updateManager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (updateInfo is not null)
            {
                updateManager.DownloadUpdatesAsync(updateInfo).GetAwaiter().GetResult();
                updateManager.ApplyUpdatesAndRestart(updateInfo);
                return 0;
            }
        }
        catch (Exception)
        {
            // 更新チェック失敗時はスキップ
        }

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
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
