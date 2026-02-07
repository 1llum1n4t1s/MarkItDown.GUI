using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Python パッケージの管理を担当するサービス
/// </summary>
public class PythonPackageManager
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="pythonExecutablePath">Python 実行ファイルのパス</param>
    /// <param name="logMessage">ログ出力関数</param>
    public PythonPackageManager(string pythonExecutablePath, Action<string> logMessage)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
    }

    /// <summary>
    /// MarkItDown 関連パッケージを自動インストール/更新する
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
    /// openai パッケージの状態をチェックし、インストールまたは最新バージョンに更新する
    /// （MarkItDown のネイティブ LLM 統合に必要）
    /// </summary>
    private async Task CheckAndInstallOpenAIPackageAsync()
    {
        try
        {
            if (!await CheckPackageInstalledAsync("openai"))
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
    private async Task<bool> CheckPackageInstalledAsync(string packageName)
    {
        try
        {
            // パッケージ名の入力検証（英数字、アンダースコア、ハイフン、ドットのみ許可）
            if (!System.Text.RegularExpressions.Regex.IsMatch(packageName, @"^[a-zA-Z0-9_.-]+$"))
            {
                System.Diagnostics.Debug.WriteLine($"Invalid package name: {packageName}");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // パッケージ名をアンダースコア化（import で使用できるようにする）
            var importName = packageName.Replace("-", "_");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"import {importName}");

            var (exitCode, _, _) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PythonVersionCheckTimeoutMs);
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to check {packageName} installation: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// pip でパッケージをインストール/更新する（非同期・デッドロック回避）
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

            var (exitCode, output, error) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PackageInstallTimeoutMs);

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
    /// markitdown パッケージの状態をチェックし、インストールまたは最新バージョンに更新する
    /// </summary>
    private async Task CheckAndUnifyMarkItDownInstallationAsync()
    {
        try
        {
            if (!await CheckPackageInstalledAsync("markitdown"))
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
    /// pip で markitdown をアンインストールする
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

            var (exitCode, output, error) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PackageUninstallTimeoutMs);

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
    /// pip で markitdown をインストール/更新する
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

            var (exitCode, output, error) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PackageInstallTimeoutMs);

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
}
