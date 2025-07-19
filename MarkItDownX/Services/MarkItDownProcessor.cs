using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDownX.Services;

/// <summary>
/// MarkItDown処理を担当するクラスなのだ
/// </summary>
public class MarkItDownProcessor
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// コンストラクタなのだ
    /// </summary>
    /// <param name="pythonExecutablePath">Python実行ファイルのパスなのだ</param>
    /// <param name="logMessage">ログ出力関数なのだ</param>
    public MarkItDownProcessor(string pythonExecutablePath, Action<string> logMessage)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
    }

    /// <summary>
    /// MarkItDownライブラリのダウンロード状況をチェックするのだ
    /// </summary>
    /// <returns>ライブラリが利用可能かどうかなのだ</returns>
    public bool CheckMarkItDownAvailability()
    {
        try
        {
            _logMessage("MarkItDownライブラリチェック開始");
                
            // アプリケーションディレクトリを取得
            var appDir = Directory.GetCurrentDirectory();
            _logMessage($"C#側アプリケーションディレクトリ: {appDir}");
                
            // Pythonスクリプトを作成して実行
            var checkScript = CreateMarkItDownCheckScript(appDir);
            var scriptPath = Path.Combine(appDir, "check_markitdown.py");
                
            try
            {
                File.WriteAllText(scriptPath, checkScript, Encoding.UTF8);
                _logMessage("チェックスクリプト作成完了");
                _logMessage($"スクリプトパス: {scriptPath}");
                _logMessage($"スクリプト内容:\n{checkScript}");
                    
                var startInfo = new ProcessStartInfo
                {
                    FileName = _pythonExecutablePath,
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = appDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _logMessage($"Python実行パス: {_pythonExecutablePath}");
                _logMessage($"Python引数: {startInfo.Arguments}");
                _logMessage($"作業ディレクトリ: {startInfo.WorkingDirectory}");

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    var startTime = DateTime.Now;
                    process.WaitForExit(30000);
                    var endTime = DateTime.Now;
                    var executionTime = endTime - startTime;
                        
                    _logMessage($"プロセス実行時間: {executionTime.TotalMilliseconds}ms");
                        
                    // プロセス終了後に出力を読み取る
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                        
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
                        _logMessage("MarkItDownライブラリチェック完了 - 利用可能");
                        return true;
                    }
                    else
                    {
                        _logMessage($"MarkItDownライブラリチェック失敗 - 終了コード: {process.ExitCode}");
                        return false;
                    }
                }
                else
                {
                    _logMessage("Pythonプロセスの開始に失敗しました");
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
                        _logMessage($"一時ファイル削除に失敗: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"MarkItDownライブラリチェック中にエラー: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// MarkItDown変換用のPythonスクリプトを実行するのだ
    /// </summary>
    /// <param name="appDir">アプリケーションディレクトリなのだ</param>
    /// <param name="filePathsJson">ファイルパスのJSON文字列なのだ</param>
    /// <param name="folderPathsJson">フォルダパスのJSON文字列なのだ</param>
    public void ExecuteMarkItDownConvertScript(string appDir, string filePathsJson, string folderPathsJson)
    {
        try
        {
            var scriptPath = Path.Combine(appDir, "Scripts", "convert_files.py");
                
            if (!File.Exists(scriptPath))
            {
                _logMessage("convert_files.pyが見つかりません");
                return;
            }
                
            _logMessage("Pythonスクリプト実行開始");
            _logMessage($"スクリプトパス: {scriptPath}");
            _logMessage($"ファイルパスJSON: {filePathsJson}");
            _logMessage($"フォルダパスJSON: {folderPathsJson}");
                
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                Arguments = $"\"{scriptPath}\" \"{filePathsJson}\" \"{folderPathsJson}\"",
                WorkingDirectory = appDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var startTime = DateTime.Now;
                process.WaitForExit(30000);
                var endTime = DateTime.Now;
                var executionTime = endTime - startTime;
                    
                _logMessage($"プロセス実行時間: {executionTime.TotalMilliseconds}ms");
                    
                // プロセス終了後に出力を読み取る
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                    
                if (!string.IsNullOrEmpty(output))
                {
                    _logMessage($"Python出力:\n{output}");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    _logMessage($"Pythonエラー:\n{error}");
                }
                    
                _logMessage($"Pythonスクリプト実行完了 - 終了コード: {process.ExitCode}");
            }
            else
            {
                _logMessage("Pythonプロセスの開始に失敗しました");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Pythonスクリプト実行中にエラー: {ex.Message}");
            _logMessage($"スタックトレース: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// MarkItDownライブラリチェック用のPythonスクリプトを作成するのだ
    /// </summary>
    /// <param name="appDir">アプリケーションディレクトリなのだ</param>
    /// <returns>Pythonスクリプトの内容なのだ</returns>
    private string CreateMarkItDownCheckScript(string appDir)
    {
        // Windowsパスのバックスラッシュをエスケープする
        var escapedAppDir = appDir.Replace("\\", "\\\\");
        
        return $@"
import os
import sys
import traceback

def log_message(message):
    print(message, flush=True)

try:
    log_message('Pythonチェックスクリプト開始')

    # アプリケーションディレクトリを使用
    log_message('アプリケーションディレクトリ: {escapedAppDir}')

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