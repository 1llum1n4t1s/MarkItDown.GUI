using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// ローカルインストールした Node.js + cli.js を経由して Claude Code を実行する。
/// </summary>
public sealed class ClaudeCodeProcessHost
{
    private const int TimeoutMs = 600_000; // 10分

    public async Task<string> ExecuteAsync(string prompt, string stdinText, CancellationToken ct = default)
    {
        var nodePath = AppPathHelper.NodeExePath;
        var cliJsPath = AppPathHelper.CliJsPath;

        if (!File.Exists(nodePath))
            throw new InvalidOperationException("Node.js が見つかりません。セットアップを実行してください。");
        if (!File.Exists(cliJsPath))
            throw new InvalidOperationException("Claude Code CLI が見つかりません。セットアップを実行してください。");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);

        var psi = new ProcessStartInfo
        {
            FileName = nodePath,
            ArgumentList = { cliJsPath, "-p", prompt },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["CI"] = "true";
        ClaudeCodeSetupService.AddNodeToPath(psi);

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(stdinText.AsMemory(), timeoutCts.Token);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Claude CLI が {TimeoutMs / 1000} 秒以内に応答しませんでした。");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Claude CLI がコード {process.ExitCode} で終了しました: {stderr}");
        }

        return stdout;
    }

    public bool IsAvailable()
    {
        return File.Exists(AppPathHelper.NodeExePath) && File.Exists(AppPathHelper.CliJsPath);
    }
}
