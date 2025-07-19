using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDownX.Services;

/// <summary>
/// Python環境の管理を担当するクラスなのだ
/// </summary>
public class PythonEnvironmentManager
{
    private string _pythonExecutablePath = string.Empty;
    private bool _pythonAvailable = false;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// コンストラクタなのだ
    /// </summary>
    /// <param name="logMessage">ログ出力関数なのだ</param>
    public PythonEnvironmentManager(Action<string> logMessage)
    {
        _logMessage = logMessage;
    }

    /// <summary>
    /// Python環境の初期化を行うのだ
    /// </summary>
    public void Initialize()
    {
        try
        {
            _logMessage("Python環境初期化開始");
            
            // システムのエンコーディングを明示的に設定
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _logMessage("エンコーディングプロバイダー登録完了");
            
            // 環境変数でエンコーディングを設定
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
            _logMessage("環境変数設定完了");
            
            // ローカルPythonの実行ファイルパスを検索
            _pythonExecutablePath = FindPythonExecutable();
            if (string.IsNullOrEmpty(_pythonExecutablePath))
            {
                _logMessage("ローカルPythonが見つかりませんでした");
                _pythonAvailable = false;
                return;
            }
                
            _logMessage($"Python実行ファイルパス: {_pythonExecutablePath}");
            _pythonAvailable = true;
            _logMessage("Python環境の初期化が完了しました");
        }
        catch (Exception ex)
        {
            _logMessage($"Python環境の初期化に失敗しました: {ex.Message}");
            _logMessage($"スタックトレース: {ex.StackTrace}");
            _pythonAvailable = false;
        }
    }

    /// <summary>
    /// ローカルPythonの実行ファイルパスを検索するのだ
    /// </summary>
    /// <returns>Python実行ファイルのパスなのだ</returns>
    private string FindPythonExecutable()
    {
        try
        {
            _logMessage("Python実行ファイル検索開始");
                
            // 一般的なPythonインストールパスをチェック
            var possiblePaths = new List<string>
            {
                @"C:\Python39\python.exe",
                @"C:\Python310\python.exe",
                @"C:\Python311\python.exe",
                @"C:\Python312\python.exe",
                @"C:\Python313\python.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python39\python.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python310\python.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python311\python.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python312\python.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Python\Python313\python.exe"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _logMessage($"Python実行ファイル発見: {path}");
                    return path;
                }
            }

            // PATH環境変数からpythonコマンドを検索
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        _logMessage($"PATHからPython発見: {output.Trim()}");
                        return "python";
                    }
                }
            }
            catch (Exception ex)
            {
                _logMessage($"PATHからのPython検索に失敗: {ex.Message}");
            }

            // python3コマンドも試行
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        _logMessage($"PATHからPython3発見: {output.Trim()}");
                        return "python3";
                    }
                }
            }
            catch (Exception ex)
            {
                _logMessage($"PATHからのPython3検索に失敗: {ex.Message}");
            }

            _logMessage("Python実行ファイルが見つかりませんでした");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logMessage($"Python実行ファイル検索中にエラー: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Pythonが利用可能かどうかを取得するのだ
    /// </summary>
    public bool IsPythonAvailable => _pythonAvailable;

    /// <summary>
    /// Python実行ファイルのパスを取得するのだ
    /// </summary>
    public string PythonExecutablePath => _pythonExecutablePath;
} 