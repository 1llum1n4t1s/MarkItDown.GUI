using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Node.js と Claude Code CLI のローカルインストール・認証を管理する。
/// UltraMDmemo の ClaudeCodeSetupService を MarkItDown.GUI 用に適応。
/// </summary>
public sealed class ClaudeCodeSetupService
{
    private const string NodeVersion = "v20.18.1";
    private const int LoginPollIntervalMs = 10_000;
    private const int LoginMaxPolls = 60; // 10分

    private static readonly HttpClient HttpClient = new();

    private readonly Action<string> _log;
    private readonly Action<double>? _progressCallback;
    private readonly Action<string>? _statusCallback;

    public ClaudeCodeSetupService(
        Action<string> log,
        Action<double>? progressCallback = null,
        Action<string>? statusCallback = null)
    {
        _log = log;
        _progressCallback = progressCallback;
        _statusCallback = statusCallback;
    }

    private static string GetNodeDownloadUrl()
        => $"https://nodejs.org/dist/{NodeVersion}/node-{NodeVersion}-win-x64.zip";

    public bool IsNodeJsInstalled => File.Exists(AppPathHelper.NodeExePath);

    public bool IsCliInstalled => File.Exists(AppPathHelper.CliJsPath);

    public string GetNodePath() => AppPathHelper.NodeExePath;

    public string GetCliJsPath() => AppPathHelper.CliJsPath;

    public async Task EnsureNodeJsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsNodeJsInstalled)
        {
            progress?.Report("Node.js は既にインストール済みです。");
            _log("Node.js は既にインストール済みなのだ。");
            return;
        }

        progress?.Report("Node.js をダウンロード中...");
        _log("Node.js をダウンロード中なのだ...");
        _statusCallback?.Invoke("Node.js をダウンロード中...");
        AppPathHelper.EnsureClaudeCodeDirectories();

        var downloadUrl = GetNodeDownloadUrl();
        var tempArchive = Path.Combine(Path.GetTempPath(), $"node-{NodeVersion}-win-x64.zip");
        try
        {
            using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(tempArchive);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                if (totalBytes > 0)
                {
                    var pct = (double)totalRead / totalBytes * 100;
                    _progressCallback?.Invoke(pct);
                }
            }
            fileStream.Close();

            progress?.Report("Node.js を展開中...");
            _log("Node.js を展開中なのだ...");
            _statusCallback?.Invoke("Node.js を展開中...");

            var tempExtract = Path.Combine(Path.GetTempPath(), $"node-extract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempExtract);

            ZipFile.ExtractToDirectory(tempArchive, tempExtract, overwriteFiles: true);

            var dirs = Directory.GetDirectories(tempExtract);
            if (dirs.Length == 0)
                throw new InvalidOperationException("Node.js アーカイブの展開に失敗しました。");
            var extractedDir = dirs[0];

            if (Directory.Exists(AppPathHelper.NodeJsDir))
                Directory.Delete(AppPathHelper.NodeJsDir, recursive: true);

            Directory.Move(extractedDir, AppPathHelper.NodeJsDir);

            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true);

            progress?.Report("Node.js のインストールが完了しました。");
            _log("Node.js のインストールが完了したのだ。");
        }
        finally
        {
            if (File.Exists(tempArchive))
                File.Delete(tempArchive);
        }
    }

    public async Task EnsureCliAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsCliInstalled)
        {
            progress?.Report("Claude Code CLI は既にインストール済みです。");
            _log("Claude Code CLI は既にインストール済みなのだ。");
            return;
        }

        if (!IsNodeJsInstalled)
            throw new InvalidOperationException("Node.js がインストールされていません。");

        progress?.Report("Claude Code CLI をインストール中...");
        _log("Claude Code CLI をインストール中なのだ...");
        _statusCallback?.Invoke("Claude Code CLI をインストール中...");
        AppPathHelper.EnsureClaudeCodeDirectories();

        var npmCliJs = Path.Combine(AppPathHelper.NodeJsDir, "node_modules", "npm", "bin", "npm-cli.js");
        var psi = new ProcessStartInfo
        {
            FileName = AppPathHelper.NodeExePath,
            ArgumentList =
            {
                npmCliJs,
                "install",
                "--global",
                "--prefix", AppPathHelper.NpmDir,
                "--cache", AppPathHelper.NpmCacheDir,
                "@anthropic-ai/claude-code"
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        AddNodeToPath(psi);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var installCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        installCts.CancelAfter(300_000); // npm install は最大5分

        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(installCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(installCts.Token);
            await process.WaitForExitAsync(installCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            _ = await stdoutTask; // stdout は現時点では使用しないが、バッファを消費する
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Claude Code CLI のインストールがタイムアウトしました（5分）。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Claude Code CLI のインストールに失敗しました (exit {process.ExitCode}): {stderr}");
        }

        progress?.Report("Claude Code CLI のインストールが完了しました。");
        _log("Claude Code CLI のインストールが完了したのだ。");
    }

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    private static bool CheckCredentialsFile()
    {
        try
        {
            if (!File.Exists(CredentialsPath))
                return false;

            var json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.TryGetProperty("accessToken", out var token)
                && token.GetString() is { Length: > 0 })
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        if (!IsNodeJsInstalled || !IsCliInstalled)
            return false;

        if (CheckCredentialsFile())
            return true;

        return await IsLoggedInViaCli(ct);
    }

    private async Task<bool> IsLoggedInViaCli(CancellationToken ct)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo.FileName = AppPathHelper.NodeExePath;
            proc.StartInfo.ArgumentList.Add(AppPathHelper.CliJsPath);
            proc.StartInfo.ArgumentList.Add("config");
            proc.StartInfo.ArgumentList.Add("get");
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.RedirectStandardInput = true;
            proc.StartInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            proc.StartInfo.Environment["CI"] = "true";
            AddNodeToPath(proc.StartInfo);

            proc.Start();
            proc.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(20_000);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                return false; // タイムアウト時は未ログインとして扱う
            }

            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw; // 呼び出し元のキャンセルは伝播する
        }
        catch
        {
            return false;
        }
    }

    public async Task RunLoginAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsNodeJsInstalled || !IsCliInstalled)
            throw new InvalidOperationException("Node.js または Claude Code CLI がインストールされていません。");

        progress?.Report("ブラウザで認証を開始します...");
        _log("ブラウザで Claude 認証を開始するのだ...");
        _statusCallback?.Invoke("ブラウザで認証を開始します...");

        var nodeBinDir = Path.GetDirectoryName(AppPathHelper.NodeExePath)!;
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"set \"PATH={nodeBinDir};%PATH%\" && \"{AppPathHelper.NodeExePath}\" \"{AppPathHelper.CliJsPath}\" || pause\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        using var loginProcess = Process.Start(psi);

        try
        {
            for (var i = 0; i < LoginMaxPolls; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(LoginPollIntervalMs, ct);

                progress?.Report($"認証完了を待っています... ({(i + 1) * LoginPollIntervalMs / 1000}秒経過)");
                _statusCallback?.Invoke($"認証完了を待っています... ({(i + 1) * LoginPollIntervalMs / 1000}秒経過)");

                if (await IsLoggedInAsync(ct))
                {
                    progress?.Report("認証が完了しました。");
                    _log("Claude 認証が完了したのだ！");
                    return;
                }
            }
        }
        finally
        {
            // loginProcess は using で Dispose されるが、終了前にプロセスを Kill する
            try { if (loginProcess is not null && !loginProcess.HasExited) loginProcess.Kill(entireProcessTree: true); } catch { }
        }

        throw new TimeoutException("認証がタイムアウトしました（10分）。");
    }

    public async Task<bool> VerifyConnectivityAsync(CancellationToken ct = default)
    {
        if (!IsNodeJsInstalled || !IsCliInstalled)
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPathHelper.NodeExePath,
                ArgumentList = { AppPathHelper.CliJsPath, "-p", "test" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["CI"] = "true";
            AddNodeToPath(psi);

            using var process = Process.Start(psi);
            if (process is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(30_000);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                return false; // タイムアウト時は false を返す
            }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw; // 呼び出し元のキャンセルは伝播する
        }
        catch
        {
            return false;
        }
    }

    internal static void AddNodeToPath(ProcessStartInfo psi)
    {
        var currentPath = psi.Environment.TryGetValue("PATH", out var existing) ? existing : "";
        psi.Environment["PATH"] = $"{AppPathHelper.NodeJsDir};{currentPath}";
    }
}
