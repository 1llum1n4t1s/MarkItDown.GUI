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
    /// ライブラリ保存用ディレクトリ（python, ffmpeg 等）。
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

    /// <summary>Node.js インストールディレクトリ。</summary>
    public static string NodeJsDir => Path.Combine(LibDirectory, "nodejs");

    /// <summary>node.exe のパス。</summary>
    public static string NodeExePath => Path.Combine(NodeJsDir, "node.exe");

    /// <summary>npm グローバルインストール先ディレクトリ。</summary>
    public static string NpmDir => Path.Combine(LibDirectory, "npm");

    /// <summary>npm キャッシュディレクトリ。</summary>
    public static string NpmCacheDir => Path.Combine(LibDirectory, "npm-cache");

    /// <summary>Claude Code CLI (cli.js) のパス。</summary>
    public static string CliJsPath => Path.Combine(NpmDir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");

    /// <summary>Claude Code CLI 用のディレクトリを作成する。</summary>
    public static void EnsureClaudeCodeDirectories()
    {
        Directory.CreateDirectory(NodeJsDir);
        Directory.CreateDirectory(NpmDir);
        Directory.CreateDirectory(NpmCacheDir);
    }

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
            var trimmed = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            var parentDir = Path.GetDirectoryName(trimmed)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(parentDir, "lib");
        }

        // デバッグ環境ではアプリ直下
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
    }

    private static string ResolveSettingsDirectory()
    {
        if (IsVelopackEnvironment)
        {
            var trimmed = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            return Path.GetDirectoryName(trimmed)
                ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }
}
