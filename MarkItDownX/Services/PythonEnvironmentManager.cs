using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDownX.Services;

/// <summary>
/// Responsible for managing Python environment
/// </summary>
public class PythonEnvironmentManager
{
    private string _pythonExecutablePath = string.Empty;
    private bool _pythonAvailable = false;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logMessage">Log output function</param>
    public PythonEnvironmentManager(Action<string> logMessage)
    {
        _logMessage = logMessage;
    }

    /// <summary>
    /// Initialize Python environment
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
            _logMessage("Starting Python executable search");

            // First, check PATH environment variable for python and python3 (most reliable)
            foreach (var pythonCommand in new[] { "python", "python3", "python.exe", "python3.exe" })
            {
                if (TryFindPythonInPath(pythonCommand))
                {
                    return pythonCommand;
                }
            }

            // Windows-specific: Check common installation paths
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            {
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
                        _logMessage($"Python executable found: {path}");
                        return path;
                    }
                }
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
    /// Try to find Python in PATH environment variable
    /// </summary>
    /// <param name="pythonCommand">Python command name (python or python3)</param>
    /// <returns>True if found and accessible</returns>
    private bool TryFindPythonInPath(string pythonCommand)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonCommand,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(TimeoutSettings.PythonVersionCheckTimeoutMs);
            if (process.ExitCode == 0)
            {
                var output = process.StandardOutput.ReadToEnd();
                _logMessage($"Found Python in PATH: {output.Trim()}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"Failed to find {pythonCommand} in PATH: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if Python is available
    /// </summary>
    public bool IsPythonAvailable => _pythonAvailable;

    /// <summary>
    /// Python実行ファイルのパスを取得するのだ
    /// </summary>
    public string PythonExecutablePath => _pythonExecutablePath;
} 