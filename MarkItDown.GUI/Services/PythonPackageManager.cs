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
    private readonly Action<string> _logError;
    private readonly Action<string> _logWarning;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="pythonExecutablePath">Python 実行ファイルのパス</param>
    /// <param name="logMessage">ログ出力関数</param>
    /// <param name="logError">エラーログ用デリゲート（省略時は logMessage を使用）</param>
    /// <param name="logWarning">警告ログ用デリゲート（省略時は logMessage を使用）</param>
    public PythonPackageManager(string pythonExecutablePath, Action<string> logMessage, Action<string>? logError = null, Action<string>? logWarning = null)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        _logWarning = logWarning ?? logMessage;
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
            _logError($"パッケージインストールでエラーなのだ: {ex.Message}");
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
                _logMessage("openaiパッケージの最新バージョンを確認中なのだ...");
            }
            // --upgrade 付きで実行し、未インストール時はインストール、インストール済み時は最新に更新
            await InstallPackageWithPipAsync("openai");
            _logMessage("openaiパッケージの確認が完了したのだ。");
        }
        catch (Exception ex)
        {
            _logError($"openai確認処理でエラーなのだ: {ex.Message}");
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
                _logMessage($"不正なパッケージ名なのだ: {packageName}");
                return;
            }

            _logMessage($"pipで{packageName}をインストール/更新中なのだ...");
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
                _logError($"pipエラー: {error.TrimEnd()}");

            if (exitCode == 0)
                _logMessage($"{packageName}のインストール/更新が完了したのだ");
            else
                _logError($"{packageName}のインストール/更新に失敗したのだ");
        }
        catch (Exception ex)
        {
            _logError($"{packageName}インストールでエラーなのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// markitdown パッケージの状態をチェックし、最新バージョンにインストール/更新する
    /// </summary>
    private async Task CheckAndUnifyMarkItDownInstallationAsync()
    {
        try
        {
            var currentVersion = await GetPackageVersionAsync("markitdown");
            if (currentVersion is null)
            {
                _logMessage("markitdownパッケージが不足しているのでpipでインストールするのだ");
            }
            else
            {
                _logMessage($"markitdown {currentVersion} がインストール済みなのだ。最新バージョンを確認中なのだ...");
            }

            await InstallMarkItDownWithPipAsync();

            // インストール後のバージョン確認
            var installedVersion = await GetPackageVersionAsync("markitdown");
            if (installedVersion is not null)
            {
                _logMessage($"markitdown {installedVersion} のインストールが完了したのだ");
            }
            else
            {
                _logError("markitdownのインストールに失敗したのだ");
            }
        }
        catch (Exception ex)
        {
            _logError($"markitdown統一処理でエラーなのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// インストール済みパッケージのバージョンを取得する
    /// </summary>
    /// <param name="packageName">パッケージ名</param>
    /// <returns>バージョン文字列（未インストールの場合は null）</returns>
    private async Task<string?> GetPackageVersionAsync(string packageName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("pip");
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add(packageName);

            var (exitCode, output, _) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PythonVersionCheckTimeoutMs);

            if (exitCode != 0 || string.IsNullOrEmpty(output))
                return null;

            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    return line["Version:".Length..].Trim();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// pip で markitdown をインストール/更新する。
    /// markitdown 0.1.x は onnxruntime&lt;=1.20.1 を要求するが、Python 3.14 では
    /// onnxruntime 1.24.1+ しか利用できないため、依存を先にインストールしてから
    /// markitdown 本体を --no-deps で入れる。
    /// </summary>
    private async Task InstallMarkItDownWithPipAsync()
    {
        try
        {
            // 1. extras 依存パッケージを先にインストール（markitdown[all] の依存群）
            _logMessage("markitdownの依存パッケージをインストール中なのだ...");
            await RunPipInstallAsync(
                "--upgrade",
                "magika>=0.6.1",
                "beautifulsoup4", "charset-normalizer", "defusedxml", "markdownify",
                "requests", "lxml", "mammoth", "olefile", "openpyxl", "pandas",
                "pdfminer-six", "pydub", "python-pptx", "speechrecognition",
                "xlrd", "youtube-transcript-api", "pathvalidate", "puremagic",
                "numpy", "azure-ai-documentintelligence", "azure-identity");
            _logMessage("markitdownの依存パッケージのインストールが完了したのだ。");

            // 2. markitdown 本体を --upgrade --no-deps で最新版をインストール
            //    （onnxruntime<=1.20.1 制約を回避するため）
            _logMessage("markitdown最新版をインストール中なのだ...");
            await RunPipInstallAsync(
                "--upgrade", "--no-deps",
                "markitdown");
            _logMessage("markitdown最新版のインストールが完了したのだ。");
        }
        catch (Exception ex)
        {
            _logError($"markitdownインストールでエラーなのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// pip install を任意の引数で実行する
    /// </summary>
    private async Task RunPipInstallAsync(params string[] args)
    {
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
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

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
                _logError($"pipエラー: {filteredError}");
            }
        }

        if (exitCode != 0)
            _logError("pipインストールに失敗したのだ");
    }
}
