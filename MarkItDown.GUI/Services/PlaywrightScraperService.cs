using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Playwright (Python) を使用したヘッドレスブラウザスクレイピングサービス。
/// ページネーション、「もっと見る」ボタン、無限スクロール等の動的コンテンツに対応する。
/// </summary>
public sealed class PlaywrightScraperService
{
    private readonly string _pythonExecutablePath;
    private readonly Action<string> _logMessage;
    private bool _isPlaywrightInstalled;
    private string? _ollamaUrl;
    private string? _ollamaModel;

    public PlaywrightScraperService(string pythonExecutablePath, Action<string> logMessage)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
    }

    /// <summary>
    /// Ollama接続情報を設定する（Pythonスクリプトに環境変数で渡す）
    /// </summary>
    public void SetOllamaConfig(string ollamaUrl, string ollamaModel)
    {
        _ollamaUrl = ollamaUrl;
        _ollamaModel = ollamaModel;
    }

    /// <summary>
    /// playwright パッケージがインストールされているかチェックし、なければインストールする
    /// </summary>
    public async Task EnsurePlaywrightInstalledAsync(CancellationToken ct = default)
    {
        if (_isPlaywrightInstalled) return;

        // パッケージチェック
        if (!await CheckPackageInstalledAsync("playwright", ct))
        {
            _logMessage("playwright パッケージをインストール中...");
            await InstallPackageAsync("playwright", ct);
        }
        else
        {
            _logMessage("playwright パッケージはインストール済みです");
        }

        _isPlaywrightInstalled = true;
    }

    /// <summary>
    /// Playwright を使ってWebページをスクレイピングし、JSONファイルとして保存する
    /// </summary>
    /// <param name="url">スクレイピング対象のURL</param>
    /// <param name="outputPath">出力先JSONファイルパス</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task ScrapeWithBrowserAsync(string url, string outputPath, CancellationToken ct = default)
    {
        await EnsurePlaywrightInstalledAsync(ct);

        var appDir = Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(appDir, "Scripts", "scrape_page.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"スクレイピングスクリプトが見つかりません: {scriptPath}");
        }

        _logMessage($"Playwright スクレイピング開始: {url}");

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

        // Ollama 設定を環境変数で渡す
        if (!string.IsNullOrEmpty(_ollamaUrl))
            startInfo.Environment["OLLAMA_URL"] = _ollamaUrl;
        if (!string.IsNullOrEmpty(_ollamaModel))
            startInfo.Environment["OLLAMA_MODEL"] = _ollamaModel;

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Python プロセスの起動に失敗しました");
        }

        // 非同期でstdout/stderrを読み取り（BeginReadLine方式でCA2024警告を回避）
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logMessage(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logMessage($"[stderr] {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // タイムアウト付きで待機
        var exited = await Task.Run(() => process.WaitForExit(TimeoutSettings.PlaywrightScrapeTimeoutMs), ct);

        if (!exited)
        {
            _logMessage("スクレイピングがタイムアウトしました。プロセスを強制終了します。");
            try { process.Kill(true); } catch { /* already exited */ }
            throw new TimeoutException("Playwright スクレイピングがタイムアウトしました");
        }

        if (process.ExitCode == 2)
        {
            throw new InvalidOperationException(
                "playwright パッケージがインストールされていません。アプリケーションを再起動してください。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"スクレイピングスクリプトがエラーで終了しました (終了コード: {process.ExitCode})");
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException($"出力ファイルが生成されませんでした: {outputPath}");
        }

        _logMessage($"Playwright スクレイピング完了: {outputPath}");
    }

    private async Task<bool> CheckPackageInstalledAsync(string packageName, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"import {packageName}");

        using var process = Process.Start(startInfo);
        if (process is null) return false;

        var exited = await Task.Run(() => process.WaitForExit(TimeoutSettings.PythonVersionCheckTimeoutMs), ct);
        return exited && process.ExitCode == 0;
    }

    private async Task InstallPackageAsync(string packageName, CancellationToken ct)
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
        startInfo.ArgumentList.Add(packageName);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            _logMessage($"{packageName} のインストールプロセス起動に失敗");
            return;
        }

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

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outputTcs.Task, errorTcs.Task);

        var output = outputSb.ToString();
        var error = errorSb.ToString();

        if (!string.IsNullOrEmpty(output))
            _logMessage($"pip: {output.TrimEnd()}");
        if (!string.IsNullOrEmpty(error) && process.ExitCode != 0)
            _logMessage($"pip エラー: {error.TrimEnd()}");

        if (process.ExitCode == 0)
            _logMessage($"{packageName} のインストール完了");
        else
            _logMessage($"{packageName} のインストールに失敗しました");
    }
}
