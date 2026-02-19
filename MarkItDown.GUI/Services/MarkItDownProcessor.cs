using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Responsible for MarkItDown processing
/// </summary>
public class MarkItDownProcessor
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly string? _ffmpegBinPath;
    private string? _claudeNodePath;
    private string? _claudeCliPath;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="pythonExecutablePath">Path to Python executable</param>
    /// <param name="logMessage">Log output function</param>
    /// <param name="ffmpegBinPath">Path to ffmpeg bin directory (optional)</param>
    /// <param name="claudeNodePath">Claude Code Node.js path (optional)</param>
    /// <param name="claudeCliPath">Claude Code CLI js path (optional)</param>
    /// <param name="logError">Error log delegate (optional, defaults to logMessage)</param>
    public MarkItDownProcessor(string pythonExecutablePath, Action<string> logMessage, string? ffmpegBinPath = null, string? claudeNodePath = null, string? claudeCliPath = null, Action<string>? logError = null)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        _ffmpegBinPath = ffmpegBinPath;
        _claudeNodePath = claudeNodePath;
        _claudeCliPath = claudeCliPath;
    }

    /// <summary>
    /// Claude Code CLI 接続情報を後から設定する
    /// </summary>
    /// <param name="nodePath">Node.js 実行パス</param>
    /// <param name="cliJsPath">Claude Code CLI の cli.js パス</param>
    public void SetClaudeConfig(string nodePath, string cliJsPath)
    {
        _claudeNodePath = nodePath;
        _claudeCliPath = cliJsPath;
    }

    /// <summary>
    /// Check MarkItDown library availability
    /// </summary>
    /// <returns>True if library is available</returns>
    public async Task<bool> CheckMarkItDownAvailabilityAsync()
    {
        try
        {
            _logMessage("MarkItDownライブラリチェック開始なのだ");
                
            // アプリケーションディレクトリを取得
            var appDir = Directory.GetCurrentDirectory();
            _logMessage($"C#側アプリケーションディレクトリなのだ: {appDir}");
                
            // Pythonスクリプトを一時ディレクトリに作成して実行（アプリ作業ディレクトリへの書き込みを避ける）
            var checkScript = CreateMarkItDownCheckScript(appDir);
            var scriptPath = Path.Combine(Path.GetTempPath(), $"check_markitdown_{Guid.NewGuid():N}.py");
                
            try
            {
                await File.WriteAllTextAsync(scriptPath, checkScript, Encoding.UTF8).ConfigureAwait(false);
                _logMessage("チェックスクリプト作成完了なのだ");

                // ProcessUtils経由でProcessStartInfoを作成
                var startInfo = ProcessUtils.CreatePythonProcessInfo(_pythonExecutablePath, scriptPath);
                startInfo.WorkingDirectory = appDir;

                if (!string.IsNullOrEmpty(_ffmpegBinPath))
                {
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.Environment["PATH"] = $"{_ffmpegBinPath};{currentPath}";
                    _logMessage($"ffmpeg PATH設定なのだ: {_ffmpegBinPath}");
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var (exitCode, output, error) = await ProcessUtils.RunAsync(
                    startInfo, TimeoutSettings.MarkItDownCheckTimeoutMs).ConfigureAwait(false);
                stopwatch.Stop();

                _logMessage($"Process execution time: {stopwatch.ElapsedMilliseconds}ms");

                if (!string.IsNullOrEmpty(output))
                {
                    _logMessage($"Python出力:\n{output}");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    _logError($"Pythonエラー:\n{error}");
                }

                if (exitCode == 0)
                {
                    _logMessage("MarkItDownライブラリチェック完了なのだ - 利用可能なのだ");
                    return true;
                }
                else if (exitCode == -1)
                {
                    _logMessage("MarkItDownライブラリチェックがタイムアウトまたはキャンセルされたのだ。");
                    return false;
                }
                else
                {
                    _logError($"MarkItDownライブラリチェック失敗なのだ - 終了コード: {exitCode}");
                    return false;
                }
            }
            finally
            {
                // 一時ファイルを削除
                if (File.Exists(scriptPath))
                {
                    try
                    {
                        File.Delete(scriptPath);
                    }
                    catch (IOException ex)
                    {
                        _logError($"一時ファイル削除に失敗したのだ: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logError($"一時ファイル削除に失敗したのだ: {ex.Message}");
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logError($"MarkItDownライブラリチェック中にI/Oエラーなのだ: {ex.Message}");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logError($"MarkItDownライブラリチェック中にエラーなのだ: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logError($"MarkItDownライブラリチェック中に予期しないエラーなのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Execute Python script for MarkItDown conversion
    /// </summary>
    /// <param name="appDir">Application directory</param>
    /// <param name="filePathsJson">JSON file path containing file paths</param>
    /// <param name="folderPathsJson">JSON file path containing folder paths</param>
    public async Task ExecuteMarkItDownConvertScriptAsync(string appDir, string filePathsJson, string folderPathsJson)
    {
        try
        {
            var scriptPath = Path.Combine(appDir, "Scripts", "convert_files.py");
                
            if (!File.Exists(scriptPath))
            {
                _logMessage("convert_files.pyが見つからないのだ");
                return;
            }
                
            _logMessage("Pythonスクリプト実行開始なのだ");
            _logMessage($"スクリプトパス: {scriptPath}");
            _logMessage($"ファイルパスJSON: {filePathsJson}");
            _logMessage($"フォルダパスJSON: {folderPathsJson}");

            var startInfo = ProcessUtils.CreatePythonProcessInfo(_pythonExecutablePath, scriptPath, filePathsJson, folderPathsJson);
            startInfo.WorkingDirectory = appDir;

            if (!string.IsNullOrEmpty(_ffmpegBinPath))
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                startInfo.Environment["PATH"] = $"{_ffmpegBinPath};{currentPath}";
            }

            if (!string.IsNullOrEmpty(_claudeNodePath))
            {
                startInfo.Environment["CLAUDE_NODE_PATH"] = _claudeNodePath;
                _logMessage($"Claude Node.js設定: {_claudeNodePath}");
            }

            if (!string.IsNullOrEmpty(_claudeCliPath))
            {
                startInfo.Environment["CLAUDE_CLI_PATH"] = _claudeCliPath;
                _logMessage($"Claude CLI設定: {_claudeCliPath}");
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logError("Pythonプロセスの開始に失敗したのだ");
                return;
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logMessage(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logError(e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new System.Threading.CancellationTokenSource(TimeoutSettings.FileConversionTimeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                stopwatch.Stop();
                _logMessage($"Process execution time: {stopwatch.ElapsedMilliseconds}ms");
                _logMessage($"Pythonスクリプト実行完了なのだ - 終了コード: {process.ExitCode}");
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                _logMessage($"Process execution time before timeout: {stopwatch.ElapsedMilliseconds}ms");
                if (!process.HasExited)
                {
                    _logMessage("ファイル変換がタイムアウトしたのだ。プロセスを強制終了するのだ。");
                    try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
                }
            }
        }
        catch (IOException ex)
        {
            _logError($"Pythonスクリプト実行中にI/Oエラーなのだ: {ex.Message}");
            _logMessage($"スタックトレースなのだ: {ex.StackTrace}");
        }
        catch (InvalidOperationException ex)
        {
            _logError($"Pythonスクリプト実行中にエラーなのだ: {ex.Message}");
            _logMessage($"スタックトレースなのだ: {ex.StackTrace}");
        }
        catch (Exception ex)
        {
            _logError($"Pythonスクリプト実行中に予期しないエラーなのだ: {ex.Message}");
            _logMessage($"スタックトレースなのだ: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Create Python script for checking MarkItDown library availability
    /// </summary>
    /// <param name="appDir">Application directory</param>
    /// <returns>Python script content</returns>
    private string CreateMarkItDownCheckScript(string appDir)
    {
        // Pythonスクリプト内で使用するため、文字列全体をエスケープする
        var escapedAppDir = appDir.Replace("\\", "\\\\").Replace("\"", "\\\"");

        return $@"
import os
import sys
import traceback

def log_message(message):
    print(message, flush=True)

try:
    log_message('Pythonチェックスクリプト開始')

    # アプリケーションディレクトリを使用
    log_message(f'アプリケーションディレクトリ: ""{escapedAppDir}""')

    # Pythonのバージョンとパスを確認
    log_message('Pythonバージョン: ' + sys.version)
    log_message('Pythonパス: ' + str(sys.path))

    # MarkItDownライブラリが利用可能かチェック
    try:
        log_message('markitdownモジュールのインポートを試行中...')
        import markitdown
        log_message('markitdownモジュールのインポートに成功')
        
        # MarkItDownクラスが利用可能かチェック
        log_message('MarkItDownクラスのインスタンス作成を試行中...')
        md = markitdown.MarkItDown()
        log_message('MarkItDownクラスのインスタンス作成に成功')
        result = True
    except ImportError as e:
        log_message('markitdownモジュールのインポートに失敗: ' + str(e))
        result = False
    except Exception as e:
        log_message('markitdownモジュールまたはクラスの利用に失敗: ' + str(e))
        log_message('エラータイプ: ' + str(type(e).__name__))
        result = False
    
    log_message('MarkItDownライブラリチェック結果: ' + str(result))
    
    if result:
        sys.exit(0)
    else:
        sys.exit(1)
        
except Exception as e:
    log_message('チェックスクリプト実行中にエラー: ' + str(e))
    log_message('エラータイプ: ' + str(type(e).__name__))
    log_message('スタックトレース: ' + traceback.format_exc())
    sys.exit(1)
";
    }
} 