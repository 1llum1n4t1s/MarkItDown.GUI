using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Responsible for MarkItDown processing
/// </summary>
public class MarkItDownProcessor
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;
    private readonly string? _ffmpegBinPath;
    private readonly string? _ollamaUrl;
    private readonly string? _ollamaModel;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="pythonExecutablePath">Path to Python executable</param>
    /// <param name="logMessage">Log output function</param>
    /// <param name="ffmpegBinPath">Path to ffmpeg bin directory (optional)</param>
    /// <param name="ollamaUrl">Ollama endpoint URL (optional)</param>
    /// <param name="ollamaModel">Ollama model name (optional)</param>
    public MarkItDownProcessor(string pythonExecutablePath, Action<string> logMessage, string? ffmpegBinPath = null, string? ollamaUrl = null, string? ollamaModel = null)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
        _ffmpegBinPath = ffmpegBinPath;
        _ollamaUrl = ollamaUrl;
        _ollamaModel = ollamaModel;
    }

    /// <summary>
    /// Check MarkItDown library availability
    /// </summary>
    /// <returns>True if library is available</returns>
    public bool CheckMarkItDownAvailability()
    {
        try
        {
            _logMessage("MarkItDownライブラリチェック開始なのだ");
                
            // アプリケーションディレクトリを取得
            var appDir = Directory.GetCurrentDirectory();
            _logMessage($"C#側アプリケーションディレクトリなのだ: {appDir}");
                
            // Pythonスクリプトを作成して実行
            var checkScript = CreateMarkItDownCheckScript(appDir);
            var scriptPath = Path.Combine(appDir, "check_markitdown.py");
                
            try
            {
                File.WriteAllText(scriptPath, checkScript, Encoding.UTF8);
                _logMessage("チェックスクリプト作成完了なのだ");
                _logMessage($"スクリプトパス: {scriptPath}");
                _logMessage($"スクリプト内容:\n{checkScript}");
                    
                var startInfo = new ProcessStartInfo
                {
                    FileName = _pythonExecutablePath,
                    WorkingDirectory = appDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(_ffmpegBinPath))
                {
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.Environment["PATH"] = $"{_ffmpegBinPath};{currentPath}";
                    _logMessage($"ffmpeg PATH設定なのだ: {_ffmpegBinPath}");
                }

                // Use argument list for secure command execution (prevents command injection)
                startInfo.ArgumentList.Add(scriptPath);

                _logMessage($"Python実行パス: {_pythonExecutablePath}");
                _logMessage($"Python引数: {startInfo.Arguments}");
                _logMessage($"作業ディレクトリ: {startInfo.WorkingDirectory}");

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    // ReadToEnd を WaitForExit より先に呼ぶ（逆にするとバッファ満杯時にデッドロックする）
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();

                    var exited = process.WaitForExit(TimeoutSettings.MarkItDownCheckTimeoutMs);
                    stopwatch.Stop();

                    _logMessage($"Process execution time: {stopwatch.ElapsedMilliseconds}ms");

                    if (!exited)
                    {
                        _logMessage("MarkItDownライブラリチェックがタイムアウトしたのだ。プロセスを強制終了するのだ。");
                        try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
                        return false;
                    }

                    if (!string.IsNullOrEmpty(output))
                    {
                        _logMessage($"Python出力:\n{output}");
                    }
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logMessage($"Pythonエラー:\n{error}");
                    }

                    if (process.ExitCode == 0)
                    {
                        _logMessage("MarkItDownライブラリチェック完了なのだ - 利用可能なのだ");
                        return true;
                    }
                    else
                    {
                        _logMessage($"MarkItDownライブラリチェック失敗なのだ - 終了コード: {process.ExitCode}");
                        return false;
                    }
                }
                else
                {
                    _logMessage("Pythonプロセスの開始に失敗したのだ");
                    _logMessage($"Python実行パス: {_pythonExecutablePath}");
                    _logMessage($"スクリプトパス: {scriptPath}");
                    _logMessage($"スクリプトファイル存在: {File.Exists(scriptPath)}");
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
                    catch (Exception ex)
                    {
                        _logMessage($"一時ファイル削除に失敗したのだ: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"MarkItDownライブラリチェック中にエラーなのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Execute Python script for MarkItDown conversion
    /// </summary>
    /// <param name="appDir">Application directory</param>
    /// <param name="filePathsJson">JSON string of file paths</param>
    /// <param name="folderPathsJson">JSON string of folder paths</param>
    public void ExecuteMarkItDownConvertScript(string appDir, string filePathsJson, string folderPathsJson)
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
                
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                WorkingDirectory = appDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrEmpty(_ffmpegBinPath))
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                startInfo.Environment["PATH"] = $"{_ffmpegBinPath};{currentPath}";
            }

            if (!string.IsNullOrEmpty(_ollamaUrl))
            {
                startInfo.Environment["OLLAMA_URL"] = _ollamaUrl;
                _logMessage($"Ollama URL設定: {_ollamaUrl}");
            }

            if (!string.IsNullOrEmpty(_ollamaModel))
            {
                startInfo.Environment["OLLAMA_MODEL"] = _ollamaModel;
                _logMessage($"Ollama Model設定: {_ollamaModel}");
            }

            // Use argument list for secure command execution (prevents command injection)
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(filePathsJson);
            startInfo.ArgumentList.Add(folderPathsJson);

            using var process = Process.Start(startInfo);
            if (process != null)
            {
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
                        _logMessage(e.Data);
                    }
                };
                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var exited = process.WaitForExit(TimeoutSettings.FileConversionTimeoutMs);
                stopwatch.Stop();

                _logMessage($"Process execution time: {stopwatch.ElapsedMilliseconds}ms");

                if (!exited)
                {
                    _logMessage("ファイル変換がタイムアウトしたのだ。プロセスを強制終了するのだ。");
                    try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
                }
                else
                {
                    _logMessage($"Pythonスクリプト実行完了なのだ - 終了コード: {process.ExitCode}");
                }
            }
            else
            {
                _logMessage("Pythonプロセスの開始に失敗したのだ");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Pythonスクリプト実行中にエラーなのだ: {ex.Message}");
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