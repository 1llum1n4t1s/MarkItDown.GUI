using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MarkItDown.GUI.Services;

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
    /// Automatically install MarkItDown package using pip
    /// </summary>
    public void InstallMarkItDownPackage()
    {
        try
        {
            CheckAndUnifyMarkItDownInstallation();
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
    
} 