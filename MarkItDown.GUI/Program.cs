using System;
using System.Threading;
using Avalonia;
using MarkItDown.GUI.Services;
using Microsoft.Win32;
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
        // Velopackフック処理（更新適用後の再起動等）
        VelopackApp.Build()
            .OnAfterUpdateFastCallback(_ =>
            {
                // 旧バージョンが登録したスタートアップ Run キーを掃除（1.0.72 以前の遺物）
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.DeleteValue("MarkItDown.GUI", throwOnMissingValue: false);
                }
                catch { /* 失敗しても続行 */ }
            })
            .Run();

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
    /// Avalonia アプリケーションをビルドする
    /// </summary>
    /// <returns>AppBuilder インスタンス</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
