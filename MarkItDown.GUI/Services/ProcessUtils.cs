using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// プロセス実行に関する共通ユーティリティ。
/// ProcessStartInfo の生成、非同期プロセス実行（デッドロック回避・タイムアウト・キャンセル対応）を提供する。
/// </summary>
public static class ProcessUtils
{
    /// <summary>
    /// Python 実行用の ProcessStartInfo を生成する（単一引数）
    /// </summary>
    /// <param name="pythonPath">Python 実行ファイルのパス</param>
    /// <param name="argument">Python に渡す引数</param>
    /// <returns>構成済みの ProcessStartInfo</returns>
    public static ProcessStartInfo CreatePythonProcessInfo(string pythonPath, string argument)
    {
        return new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { argument }
        };
    }

    /// <summary>
    /// Python 実行用の ProcessStartInfo を生成する（複数引数）
    /// </summary>
    public static ProcessStartInfo CreatePythonProcessInfo(string pythonPath, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    /// <summary>
    /// コマンドが PATH 上に存在するかチェックする
    /// </summary>
    public static bool TryCheckCommandVersion(string command, int timeoutMs, Action<string> logMessage)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Arguments プロパティの代わりに ArgumentList を使用（セキュアコマンド実行）
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var exited = process.WaitForExit(timeoutMs);
            return exited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logMessage?.Invoke($"Command check failed: {command} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// プロセスを非同期で実行し、stdout/stderr をデッドロックなく読み取る。
    /// BeginOutputReadLine/BeginErrorReadLine + TaskCompletionSource パターンにより、
    /// ストリーム完了を確実に検知する。タイムアウト時はプロセスを強制終了する。
    /// </summary>
    /// <param name="startInfo">プロセス起動情報（RedirectStandardOutput/Error = true であること）</param>
    /// <param name="timeoutMs">タイムアウト（ミリ秒）</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <returns>終了コード、stdout、stderr のタプル</returns>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo, int timeoutMs, CancellationToken ct = default)
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

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            await Task.WhenAll(outputTcs.Task, errorTcs.Task);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
            }

            if (ct.IsCancellationRequested)
                return (-1, outputSb.ToString(), "プロセスがキャンセルされました");

            return (-1, outputSb.ToString(), "プロセスがタイムアウトしました");
        }

        return (process.ExitCode, outputSb.ToString(), errorSb.ToString());
    }
}
