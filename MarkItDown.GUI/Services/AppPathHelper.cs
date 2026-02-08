using System;
using System.IO;

namespace MarkItDown.GUI.Services;

/// <summary>
/// アプリケーションのパス解決ヘルパー。
/// Velopack 環境では current フォルダが更新時に置き換わるため、
/// ダウンロードしたライブラリは current の親階層に配置する。
/// </summary>
public static class AppPathHelper
{
    private static readonly Lazy<string> _libDirectory = new(ResolveLibDirectory);
    private static readonly Lazy<string> _settingsDirectory = new(ResolveSettingsDirectory);

    /// <summary>
    /// ライブラリ保存用ディレクトリ（python, ffmpeg, ollama 等）。
    /// Velopack 環境: %LOCALAPPDATA%\MarkItDown.GUI\lib\
    /// デバッグ環境: {AppBaseDir}\lib\
    /// </summary>
    public static string LibDirectory => _libDirectory.Value;

    /// <summary>
    /// 設定ファイル保存用ディレクトリ。
    /// Velopack 環境: %LOCALAPPDATA%\MarkItDown.GUI\
    /// デバッグ環境: {AppBaseDir}\
    /// </summary>
    public static string SettingsDirectory => _settingsDirectory.Value;

    /// <summary>
    /// アプリケーション実行ファイルのディレクトリ。
    /// </summary>
    public static string AppBaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>
    /// Velopack の current フォルダ内で実行されているかどうかを判定する。
    /// </summary>
    public static bool IsVelopackEnvironment
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            return baseDir.EndsWith(@"\current", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ResolveLibDirectory()
    {
        if (IsVelopackEnvironment)
        {
            // current の親 = %LOCALAPPDATA%\MarkItDown.GUI\
            var parentDir = Path.GetDirectoryName(
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'))!;
            return Path.Combine(parentDir, "lib");
        }

        // デバッグ環境ではアプリ直下
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
    }

    private static string ResolveSettingsDirectory()
    {
        if (IsVelopackEnvironment)
        {
            var parentDir = Path.GetDirectoryName(
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'))!;
            return parentDir;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }
}
