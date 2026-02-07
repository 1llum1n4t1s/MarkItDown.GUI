using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            CheckAndInstallOpenAIPackage();
        }
        catch (Exception ex)
        {
            _logMessage($"パッケージインストールでエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// openaiパッケージの状態をチェックし、インストールまたは最新バージョンに更新する（MarkItDownのネイティブLLM統合に必要）
    /// </summary>
    private void CheckAndInstallOpenAIPackage()
    {
        try
        {
            if (!CheckPackageInstalled("openai"))
            {
                _logMessage("openaiパッケージが不足しているのでpipでインストールするのだ");
            }
            else
            {
                _logMessage("openaiパッケージの最新バージョンを確認中...");
            }
            // --upgrade 付きで実行し、未インストール時はインストール、インストール済み時は最新に更新
            InstallPackageWithPip("openai");
        }
        catch (Exception ex)
        {
            _logMessage($"openai確認処理でエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// パッケージがインストールされているかチェックする
    /// </summary>
    /// <param name="packageName">パッケージ名</param>
    /// <returns>インストールされているかどうか</returns>
    private bool CheckPackageInstalled(string packageName)
    {
        try
        {
            // パッケージ名の入力検証（英数字、アンダースコア、ハイフン、ドットのみ許可）
            if (!System.Text.RegularExpressions.Regex.IsMatch(packageName, @"^[a-zA-Z0-9_.-]+$"))
            {
                System.Diagnostics.Debug.WriteLine($"Invalid package name: {packageName}");
                return false;
            }

            var checkInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // パッケージ名をアンダースコア化（importで使用できるようにする）
            var importName = packageName.Replace("-", "_");

            checkInfo.ArgumentList.Add("-c");
            checkInfo.ArgumentList.Add($"import {importName}");

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
            System.Diagnostics.Debug.WriteLine($"Failed to check {packageName} installation: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// pipでパッケージをインストールする
    /// </summary>
    /// <param name="packageName">パッケージ名</param>
    private void InstallPackageWithPip(string packageName)
    {
        try
        {
            // パッケージ名の入力検証（英数字、アンダースコア、ハイフンのみ許可）
            if (!System.Text.RegularExpressions.Regex.IsMatch(packageName, @"^[a-zA-Z0-9_-]+$"))
            {
                _logMessage($"不正なパッケージ名: {packageName}");
                return;
            }

            _logMessage($"pipで{packageName}をインストール/更新中...");
            var installInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            installInfo.ArgumentList.Add("-m");
            installInfo.ArgumentList.Add("pip");
            installInfo.ArgumentList.Add("install");
            installInfo.ArgumentList.Add("--upgrade");
            installInfo.ArgumentList.Add(packageName);

            using var installProc = Process.Start(installInfo);
            if (installProc != null)
            {
                var output = installProc.StandardOutput.ReadToEnd();
                var error = installProc.StandardError.ReadToEnd();
                installProc.WaitForExit(TimeoutSettings.PackageInstallTimeoutMs);
                _logMessage($"pip出力: {output}");
                if (!string.IsNullOrEmpty(error))
                {
                    _logMessage($"pipエラー: {error}");
                }

                if (installProc.ExitCode == 0)
                {
                    _logMessage($"{packageName}のインストールが完了したのだ");
                }
                else
                {
                    _logMessage($"{packageName}のインストールに失敗したのだ");
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"{packageName}インストールでエラー: {ex.Message}");
        }
    }
    

    
    /// <summary>
    /// markitdownパッケージの状態をチェックし、インストールまたは最新バージョンに更新する
    /// </summary>
    private void CheckAndUnifyMarkItDownInstallation()
    {
        try
        {
            if (!CheckMarkItDownInstalled())
            {
                _logMessage("markitdownパッケージが不足しているのでpipでインストールするのだ");
            }
            else
            {
                _logMessage("markitdownパッケージの最新バージョンを確認中...");
            }
            // --upgrade 付きで実行し、未インストール時はインストール、インストール済み時は最新に更新
            InstallMarkItDownWithPip();
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
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            checkInfo.ArgumentList.Add("-c");
            checkInfo.ArgumentList.Add("import markitdown");

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
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            uninstallInfo.ArgumentList.Add("-m");
            uninstallInfo.ArgumentList.Add("pip");
            uninstallInfo.ArgumentList.Add("uninstall");
            uninstallInfo.ArgumentList.Add("markitdown");
            uninstallInfo.ArgumentList.Add("-y");

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
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            installInfo.ArgumentList.Add("-m");
            installInfo.ArgumentList.Add("pip");
            installInfo.ArgumentList.Add("install");
            installInfo.ArgumentList.Add("--upgrade");
            installInfo.ArgumentList.Add("markitdown[all]");

            using var installProc = Process.Start(installInfo);
            if (installProc != null)
            {
                var output = installProc.StandardOutput.ReadToEnd();
                var error = installProc.StandardError.ReadToEnd();
                installProc.WaitForExit(TimeoutSettings.PackageInstallTimeoutMs);
                _logMessage($"pip出力: {output}");

                if (!string.IsNullOrEmpty(error))
                {
                    var errorLines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var filteredErrors = errorLines.Where(line =>
                        !line.Contains("WARNING: The script") &&
                        !line.Contains("is installed in") &&
                        !line.Contains("which is not on PATH") &&
                        !line.Contains("Consider adding this directory to PATH"));
                    var filteredError = string.Join('\n', filteredErrors).Trim();
                    if (!string.IsNullOrEmpty(filteredError))
                    {
                        _logMessage($"pipエラー: {filteredError}");
                    }
                }

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