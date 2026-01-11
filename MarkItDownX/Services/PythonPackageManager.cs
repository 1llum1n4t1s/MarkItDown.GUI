using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDownX.Services;

/// <summary>
/// Responsible for managing Python packages
/// </summary>
public class PythonPackageManager
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="pythonExecutablePath">Path to Python executable</param>
    /// <param name="logMessage">Log output function</param>
    public PythonPackageManager(string pythonExecutablePath, Action<string> logMessage)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
    }

    /// <summary>
    /// Automatically install MarkItDown and FFmpeg using pip
    /// </summary>
    public void InstallMarkItDownPackage()
    {
        try
        {
            // markitdownパッケージの状態をチェックして統一するのだ
            CheckAndUnifyMarkItDownInstallation();
            
            // ffmpegのインストールを試行するのだ
            InstallFfmpegWithWinget();
        }
        catch (Exception ex)
        {
            _logMessage($"パッケージインストールでエラー: {ex.Message}");
        }
    }
    

    
    /// <summary>
    /// markitdownパッケージの状態をチェックして統一するのだ
    /// </summary>
    private void CheckAndUnifyMarkItDownInstallation()
    {
        try
        {
            // markitdownがインストールされているかチェック
            if (!CheckMarkItDownInstalled())
            {
                _logMessage("markitdownパッケージが不足しているのでpipでインストールするのだ");
                InstallMarkItDownWithPip();
                return;
            }
            
            _logMessage("markitdownパッケージはインストール済みなのだ");
        }
        catch (Exception ex)
        {
            _logMessage($"markitdown統一処理でエラー: {ex.Message}");
        }
    }
    
    /// <summary>
    /// markitdownパッケージがインストールされているかチェックするのだ
    /// </summary>
    /// <returns>markitdownがインストールされているかどうかなのだ</returns>
    private bool CheckMarkItDownInstalled()
    {
        try
        {
            var checkInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                Arguments = "-c \"import markitdown\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var checkProc = Process.Start(checkInfo);
            if (checkProc != null)
            {
                checkProc.WaitForExit(TimeoutSettings.PythonVersionCheckTimeoutMs);
                return checkProc.ExitCode == 0;
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to check markitdown installation: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// pipでmarkitdownをアンインストールするのだ
    /// </summary>
    private void UninstallMarkItDownWithPip()
    {
        try
        {
            _logMessage("pipでmarkitdownをアンインストール中...");
            var uninstallInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                Arguments = "-m pip uninstall markitdown -y",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var uninstallProc = Process.Start(uninstallInfo);
            if (uninstallProc != null)
            {
                string output = uninstallProc.StandardOutput.ReadToEnd();
                string error = uninstallProc.StandardError.ReadToEnd();
                uninstallProc.WaitForExit(TimeoutSettings.PackageUninstallTimeoutMs);
                _logMessage($"pipアンインストール出力: {output}");
                if (!string.IsNullOrEmpty(error))
                    _logMessage($"pipアンインストールエラー: {error}");
                
                if (uninstallProc.ExitCode == 0)
                {
                    _logMessage("markitdownのアンインストールが完了したのだ");
                }
                else
                {
                    _logMessage("markitdownのアンインストールに失敗したのだ");
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"markitdownアンインストールでエラー: {ex.Message}");
        }
    }
    
    /// <summary>
    /// pipでmarkitdownをインストールするのだ
    /// </summary>
    private void InstallMarkItDownWithPip()
    {
        try
        {
            _logMessage("pipでmarkitdownをインストール中...");
            var installInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                Arguments = "-m pip install markitdown[all]",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using var installProc = Process.Start(installInfo);
            if (installProc != null)
            {
                string output = installProc.StandardOutput.ReadToEnd();
                string error = installProc.StandardError.ReadToEnd();
                installProc.WaitForExit(TimeoutSettings.PackageInstallTimeoutMs);
                _logMessage($"pip出力: {output}");
                if (!string.IsNullOrEmpty(error))
                    _logMessage($"pipエラー: {error}");
                
                if (installProc.ExitCode == 0)
                {
                    _logMessage("markitdownのインストールが完了したのだ");
                }
                else
                {
                    _logMessage("markitdownのインストールに失敗したのだ");
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"markitdownインストールでエラー: {ex.Message}");
        }
    }
    
    /// <summary>
    /// ffmpegをインストールするのだ
    /// </summary>
    private void InstallFfmpegWithWinget()
    {
        try
        {
            _logMessage("ffmpegのインストールを試行中...");
            
            // ffmpegが既にインストールされているかチェック
            _logMessage("ffmpegのインストール状況をチェック中...");
            var checkFfmpegInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            try
            {
                using var checkFfmpegProc = Process.Start(checkFfmpegInfo);
                if (checkFfmpegProc != null)
                {
                    string output = checkFfmpegProc.StandardOutput.ReadToEnd();
                    string error = checkFfmpegProc.StandardError.ReadToEnd();
                    checkFfmpegProc.WaitForExit(TimeoutSettings.FFmpegCheckTimeoutMs);
                    
                    if (checkFfmpegProc.ExitCode == 0)
                    {
                        _logMessage($"ffmpegは既にインストール済みなのだ: {output.Trim()}");
                        return;
                    }
                    else
                    {
                        _logMessage($"ffmpegチェックでエラー: {error}");
                    }
                }
                else
                {
                    _logMessage("ffmpegプロセスの開始に失敗したのだ");
                }
            }
            catch (Exception ex)
            {
                _logMessage($"ffmpegが見つからないため、インストールを続行するのだ: {ex.Message}");
            }
            
            // wingetでffmpegがインストールされているかチェック
            _logMessage("wingetでffmpegのインストール状況をチェック中...");
            var checkWingetInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list Gyan.FFmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            try
            {
                using var checkWingetProc = Process.Start(checkWingetInfo);
                if (checkWingetProc != null)
                {
                    string output = checkWingetProc.StandardOutput.ReadToEnd();
                    string error = checkWingetProc.StandardError.ReadToEnd();
                    checkWingetProc.WaitForExit(TimeoutSettings.FFmpegCheckTimeoutMs);
                    
                    if (checkWingetProc.ExitCode == 0 && output.Contains("Gyan.FFmpeg"))
                    {
                        _logMessage("wingetでffmpegがインストール済みだが、PATHに追加されていないのだ");
                        _logMessage("ffmpegをPATHに追加するか、再起動が必要なのだ");
                        return;
                    }
                    else
                    {
                        _logMessage("wingetでffmpegがインストールされていないのだ");
                    }
                }
            }
            catch (Exception ex)
            {
                _logMessage($"wingetチェックでエラー: {ex.Message}");
            }
            
            // wingetを使用してffmpegをインストール
            _logMessage("wingetを使用してffmpegをインストール中...");
            var wingetInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install Gyan.FFmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            try
            {
                using var wingetProc = Process.Start(wingetInfo);
                if (wingetProc != null)
                {
                    string output = wingetProc.StandardOutput.ReadToEnd();
                    string error = wingetProc.StandardError.ReadToEnd();
                    wingetProc.WaitForExit(TimeoutSettings.FFmpegInstallTimeoutMs);
                    _logMessage($"winget出力: {output}");
                    if (!string.IsNullOrEmpty(error))
                        _logMessage($"wingetエラー: {error}");
                    
                    if (wingetProc.ExitCode == 0)
                    {
                        _logMessage("ffmpegのインストールが完了したのだ");
                    }
                    else
                    {
                        _logMessage("wingetでのffmpegインストールに失敗したのだ");
                    }
                }
                else
                {
                    _logMessage("wingetが見つからないため、ffmpegのインストールをスキップするのだ");
                }
            }
            catch (Exception ex)
            {
                _logMessage($"wingetでエラー: {ex.Message}");
                _logMessage("ffmpegのインストールに失敗したが、MarkItDownは動作するのだ");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"ffmpegインストール処理でエラー: {ex.Message}");
        }
    }
    

} 