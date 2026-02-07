using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    public async Task InstallMarkItDownPackageAsync()
    {
        try
        {
            await CheckAndUnifyMarkItDownInstallationAsync();
            await CheckAndInstallOpenAIPackageAsync();
        }
        catch (Exception ex)
        {
            _logMessage($"パッケージインストールでエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// openaiパッケージの状態をチェックし、インストールまたは最新バージョンに更新する（MarkItDownのネイティブLLM統合に必要）
    /// </summary>
    private async Task CheckAndInstallOpenAIPackageAsync()
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
            await InstallPackageWithPipAsync("openai");
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
    /// pipでパッケージをインストール/更新する（非同期・デッドロック回避）
    /// </summary>
    /// <param name="packageName">パッケージ名</param>
    private async Task InstallPackageWithPipAsync(string packageName)
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
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pip");
            startInfo.ArgumentList.Add("install");
            startInfo.ArgumentList.Add("--upgrade");
            startInfo.ArgumentList.Add(packageName);

            var (exitCode, output, error) = await RunProcessAsync(startInfo, TimeoutSettings.PackageInstallTimeoutMs);

            if (!string.IsNullOrEmpty(output))
                _logMessage($"pip出力: {output.TrimEnd()}");
            if (!string.IsNullOrEmpty(error) && exitCode != 0)
                _logMessage($"pipエラー: {error.TrimEnd()}");

            if (exitCode == 0)
                _logMessage($"{packageName}のインストール/更新が完了したのだ");
            else
                _logMessage($"{packageName}のインストール/更新に失敗したのだ");
        }
        catch (Exception ex)
        {
            _logMessage($"{packageName}インストールでエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// markitdownパッケージの状態をチェックし、インストールまたは最新バージョンに更新する
    /// </summary>
    private async Task CheckAndUnifyMarkItDownInstallationAsync()
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
            await InstallMarkItDownWithPipAsync();
        }
        catch (Exception ex)
        {
            _logMessage($"markitdown統一処理でエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// markitdownパッケージがインストールされているかチェックする
    /// </summary>
    /// <returns>markitdownがインストールされているかどうか</returns>
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
    /// pipでmarkitdownをアンインストールする
    /// </summary>
    private async Task UninstallMarkItDownWithPipAsync()
    {
        try
        {
            _logMessage("pipでmarkitdownをアンインストール中...");
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pip");
            startInfo.ArgumentList.Add("uninstall");
            startInfo.ArgumentList.Add("markitdown");
            startInfo.ArgumentList.Add("-y");

            var (exitCode, output, error) = await RunProcessAsync(startInfo, TimeoutSettings.PackageUninstallTimeoutMs);

            if (!string.IsNullOrEmpty(output))
                _logMessage($"pipアンインストール出力: {output.TrimEnd()}");
            if (!string.IsNullOrEmpty(error))
                _logMessage($"pipアンインストールエラー: {error.TrimEnd()}");

            if (exitCode == 0)
                _logMessage("markitdownのアンインストールが完了したのだ");
            else
                _logMessage("markitdownのアンインストールに失敗したのだ");
        }
        catch (Exception ex)
        {
            _logMessage($"markitdownアンインストールでエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// pipでmarkitdownをインストール/更新する
    /// </summary>
    private async Task InstallMarkItDownWithPipAsync()
    {
        try
        {
            _logMessage("pipでmarkitdownをインストール/更新中...");
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pip");
            startInfo.ArgumentList.Add("install");
            startInfo.ArgumentList.Add("--upgrade");
            startInfo.ArgumentList.Add("markitdown[all]");

            var (exitCode, output, error) = await RunProcessAsync(startInfo, TimeoutSettings.PackageInstallTimeoutMs);

            if (!string.IsNullOrEmpty(output))
                _logMessage($"pip出力: {output.TrimEnd()}");

            if (!string.IsNullOrEmpty(error) && exitCode != 0)
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

            if (exitCode == 0)
                _logMessage("markitdownのインストール/更新が完了したのだ");
            else
                _logMessage("markitdownのインストール/更新に失敗したのだ");
        }
        catch (Exception ex)
        {
            _logMessage($"markitdownインストールでエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// プロセスを非同期で実行し、stdout/stderrをデッドロックなく読み取る
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo startInfo, int timeoutMs)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, "", "プロセスの起動に失敗しました");

        var outputSb = new StringBuilder();
        var errorSb = new StringBuilder();
        var outputTcs = new TaskCompletionSource();
        var errorTcs = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) outputSb.AppendLine(e.Data);
            else outputTcs.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) errorSb.AppendLine(e.Data);
            else errorTcs.TrySetResult();
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new System.Threading.CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(outputTcs.Task, errorTcs.Task);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { /* already exited */ }
            }
            return (-1, outputSb.ToString(), "プロセスがタイムアウトしました");
        }

        return (process.ExitCode, outputSb.ToString(), errorSb.ToString());
    }
}
