using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly Action<string> _logError;
    private readonly Action<string>? _statusCallback;
    private bool _dependenciesInstalled;
    private string? _claudeNodePath;
    private string? _claudeCliPath;

    public PlaywrightScraperService(string pythonExecutablePath, Action<string> logMessage, Action<string>? statusCallback = null, Action<string>? logError = null)
    {
        _pythonExecutablePath = pythonExecutablePath;
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        _statusCallback = statusCallback;
    }

    /// <summary>
    /// Claude Code CLI接続情報を設定する（Pythonスクリプトに環境変数で渡す）
    /// </summary>
    public void SetClaudeConfig(string nodePath, string cliJsPath)
    {
        _claudeNodePath = nodePath;
        _claudeCliPath = cliJsPath;
    }

    /// <summary>
    /// 必要な Python パッケージ (playwright) がインストールされているかチェックし、
    /// なければインストール、あれば最新バージョンに更新する
    /// </summary>
    public async Task EnsureDependenciesInstalledAsync(CancellationToken ct = default)
    {
        if (_dependenciesInstalled) return;

        _statusCallback?.Invoke("依存パッケージを確認中...");

        // playwright パッケージチェック・更新
        if (!await CheckPackageInstalledAsync("playwright", ct).ConfigureAwait(false))
        {
            _logMessage("playwright パッケージをインストール中なのだ...");
        }
        else
        {
            _logMessage("playwright パッケージの最新バージョンを確認中なのだ...");
        }
        await InstallPackageAsync("playwright", ct).ConfigureAwait(false);

        _dependenciesInstalled = true;
    }

    /// <summary>
    /// Playwright を使ってWebページをスクレイピングし、JSONファイルとして保存する
    /// </summary>
    /// <param name="url">スクレイピング対象のURL</param>
    /// <param name="outputPath">出力先JSONファイルパス</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task ScrapeWithBrowserAsync(string url, string outputPath, CancellationToken ct = default)
    {
        await EnsureDependenciesInstalledAsync(ct).ConfigureAwait(false);

        var appDir = Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(appDir, "Scripts", "scrape_page.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"スクレイピングスクリプトが見つかりません: {scriptPath}");
        }

        _statusCallback?.Invoke("Playwright でページを読み込み中...");
        _logMessage($"Playwright スクレイピング開始なのだ: {url}");

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

        // Claude Code CLI 設定を環境変数で渡す
        if (!string.IsNullOrEmpty(_claudeNodePath))
            startInfo.Environment["CLAUDE_NODE_PATH"] = _claudeNodePath;
        if (!string.IsNullOrEmpty(_claudeCliPath))
            startInfo.Environment["CLAUDE_CLI_PATH"] = _claudeCliPath;

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Python プロセスの起動に失敗しました");
        }

        // アイドルタイムアウト: 最終出力から一定時間無出力ならタイムアウト
        // Pythonスクリプトがログを出力し続ける限りタイムアウトしない
        using var idleCts = new CancellationTokenSource();

        void ResetIdleTimer()
        {
            try
            {
                idleCts.CancelAfter(TimeoutSettings.PlaywrightScrapeTimeoutMs);
            }
            catch (ObjectDisposedException)
            {
                // プロセス終了後にタイマーリセットが呼ばれた場合は無視
            }
        }

        // 初回タイマー開始
        ResetIdleTimer();

        // 非同期でstdout/stderrを読み取り（BeginReadLine方式でCA2024警告を回避）
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage(e.Data);
                ResetIdleTimer();
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage($"[stderr] {e.Data}");
                ResetIdleTimer();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // アイドルタイムアウトとキャンセルトークン付きで待機
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                _logMessage("スクレイピングがタイムアウトまたはキャンセルされたのだ。プロセスを強制終了するのだ。");
                try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
            }
            if (ct.IsCancellationRequested) throw;
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

        _logMessage($"Playwright スクレイピング完了なのだ: {outputPath}");
    }

    /// <summary>
    /// ブラウザを使わない HTTP ベースのスクレイピング。
    /// requests + BeautifulSoup + Claude ガイド型で処理する。
    /// </summary>
    public async Task ScrapeWithHttpAsync(string url, string outputPath, CancellationToken ct = default)
    {
        var appDir = Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(appDir, "Scripts", "scrape_page_http.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"HTTP スクレイピングスクリプトが見つかりません: {scriptPath}");
        }

        _statusCallback?.Invoke("HTTP でページを取得中...");
        _logMessage($"HTTP スクレイピング開始なのだ: {url}");

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

        // Claude Code CLI 設定を環境変数で渡す
        if (!string.IsNullOrEmpty(_claudeNodePath))
            startInfo.Environment["CLAUDE_NODE_PATH"] = _claudeNodePath;
        if (!string.IsNullOrEmpty(_claudeCliPath))
            startInfo.Environment["CLAUDE_CLI_PATH"] = _claudeCliPath;

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Python プロセスの起動に失敗しました");
        }

        using var idleCts = new CancellationTokenSource();

        void ResetIdleTimer()
        {
            try
            {
                idleCts.CancelAfter(TimeoutSettings.PlaywrightScrapeTimeoutMs);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        ResetIdleTimer();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage(e.Data);
                ResetIdleTimer();
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage($"[stderr] {e.Data}");
                ResetIdleTimer();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                _logMessage("HTTP スクレイピングがタイムアウトまたはキャンセルされたのだ。プロセスを強制終了するのだ。");
                try { process.Kill(true); } catch (InvalidOperationException) { }
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("HTTP スクレイピングがタイムアウトしました");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"HTTP スクレイピングスクリプトがエラーで終了しました (終了コード: {process.ExitCode})");
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException($"出力ファイルが生成されませんでした: {outputPath}");
        }

        _logMessage($"HTTP スクレイピング完了なのだ: {outputPath}");
    }

    /// <summary>
    /// X/Twitter 専用スクレイピング。Pythonスクリプト scrape_x.py を起動して
    /// 全ツイート取得 + オリジナル品質画像ダウンロードを行う。
    /// </summary>
    /// <param name="username">X/Twitterのユーザー名（@なし）</param>
    /// <param name="outputDir">出力先ディレクトリ（username/ サブフォルダが作成される）</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task ScrapeXTwitterAsync(string username, string outputDir, CancellationToken ct = default)
    {
        await EnsureDependenciesInstalledAsync(ct).ConfigureAwait(false);

        // httpx パッケージの追加インストール（画像ダウンロード用）
        if (!await CheckPackageInstalledAsync("httpx", ct).ConfigureAwait(false))
        {
            _logMessage("httpx パッケージをインストール中なのだ...");
            await InstallPackageAsync("httpx", ct).ConfigureAwait(false);
        }

        // browser-cookie3 パッケージの追加インストール（通常ChromeのCookie取り込み用）
        if (!await CheckPackageInstalledAsync("browser_cookie3", ct).ConfigureAwait(false))
        {
            _logMessage("browser-cookie3 パッケージをインストール中なのだ...");
            await InstallPackageAsync("browser-cookie3", ct).ConfigureAwait(false);
        }

        var appDir = Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(appDir, "Scripts", "scrape_x.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"X.comスクレイピングスクリプトが見つかりません: {scriptPath}");
        }

        _statusCallback?.Invoke($"X.com (@{username}) のスクレイピング準備中...");
        _logMessage($"X.com 専用スクレイピング開始なのだ: @{username}");

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

        // セッションファイルパスを環境変数で渡す
        var sessionPath = Path.Combine(appDir, "lib", "playwright", "x_session.json");
        startInfo.Environment["X_SESSION_PATH"] = sessionPath;

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add(outputDir);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Python プロセスの起動に失敗しました");
        }

        // アイドルタイムアウト: X.com用は長めに設定（10分）
        using var idleCts = new CancellationTokenSource();

        void ResetIdleTimer()
        {
            try
            {
                idleCts.CancelAfter(TimeoutSettings.XTwitterIdleTimeoutMs);
            }
            catch (ObjectDisposedException)
            {
                // プロセス終了後にタイマーリセットが呼ばれた場合は無視
            }
        }

        ResetIdleTimer();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage(e.Data);
                ResetIdleTimer();
                // Python のログから進捗をオーバーレイに反映
                UpdateXTwitterStatus(e.Data, username);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage($"[stderr] {e.Data}");
                ResetIdleTimer();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                _logMessage("X.comスクレイピングがタイムアウトまたはキャンセルされたのだ。プロセスを強制終了するのだ。");
                try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("X.com スクレイピングがタイムアウトしました");
        }

        if (process.ExitCode == 2)
        {
            throw new InvalidOperationException(
                "playwright パッケージがインストールされていません。アプリケーションを再起動してください。");
        }

        if (process.ExitCode == 3)
        {
            // セッション切れ: プロファイルディレクトリを削除
            var sessionDir = Path.GetDirectoryName(sessionPath);
            if (sessionDir is null)
            {
                throw new InvalidOperationException(
                    "X.comのセッションが無効です。再度実行するとブラウザが開くので、ログインしてください。");
            }
            var profileDir = Path.Combine(sessionDir, "x_profile");
            if (Directory.Exists(profileDir))
            {
                try { Directory.Delete(profileDir, true); }
                catch { /* 削除失敗は無視 */ }
            }
            throw new InvalidOperationException(
                "X.comのセッションが無効です。再度実行するとブラウザが開くので、ログインしてください。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"X.comスクレイピングスクリプトがエラーで終了しました (終了コード: {process.ExitCode})");
        }

        _logMessage($"X.com スクレイピング完了なのだ: @{username}");
    }

    /// <summary>
    /// Instagram 専用スクレイピング。Pythonスクリプト scrape_instagram.py を起動して
    /// 全投稿のメディア（画像・動画）をダウンロードする。
    /// </summary>
    /// <param name="username">Instagramのユーザー名（@なし）</param>
    /// <param name="outputDir">出力先ディレクトリ</param>
    /// <param name="ct">キャンセルトークン</param>
    public async Task ScrapeInstagramAsync(string username, string outputDir, CancellationToken ct = default)
    {
        await EnsureDependenciesInstalledAsync(ct).ConfigureAwait(false);

        // openai パッケージの追加インストール
        if (!await CheckPackageInstalledAsync("openai", ct).ConfigureAwait(false))
        {
            _logMessage("openai パッケージをインストール中なのだ...");
        }
        else
        {
            _logMessage("openai パッケージの最新バージョンを確認中なのだ...");
        }
        await InstallPackageAsync("openai", ct).ConfigureAwait(false);

        // instaloader パッケージの追加インストール
        if (!await CheckPackageInstalledAsync("instaloader", ct).ConfigureAwait(false))
        {
            _logMessage("instaloader パッケージをインストール中なのだ...");
        }
        else
        {
            _logMessage("instaloader パッケージの最新バージョンを確認中なのだ...");
        }
        await InstallPackageAsync("instaloader", ct).ConfigureAwait(false);

        var appDir = Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(appDir, "Scripts", "scrape_instagram.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Instagramスクレイピングスクリプトが見つかりません: {scriptPath}");
        }

        _statusCallback?.Invoke($"Instagram (@{username}) のスクレイピング準備中...");
        _logMessage($"Instagram 専用スクレイピング開始なのだ: @{username}");

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

        // セッションディレクトリを環境変数で渡す
        var sessionDir = Path.Combine(appDir, "lib", "playwright", "instagram_session");
        startInfo.Environment["IG_SESSION_DIR"] = sessionDir;

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add(outputDir);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Python プロセスの起動に失敗しました");
        }

        // アイドルタイムアウト: Instagram用は長めに設定（10分）
        using var idleCts = new CancellationTokenSource();

        void ResetIdleTimer()
        {
            try
            {
                idleCts.CancelAfter(TimeoutSettings.InstagramIdleTimeoutMs);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        ResetIdleTimer();

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            _logMessage(e.Data);
            ResetIdleTimer();
            UpdateInstagramStatus(e.Data, username);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logMessage($"[stderr] {e.Data}");
                ResetIdleTimer();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                _logMessage("Instagramスクレイピングがタイムアウトまたはキャンセルされたのだ。プロセスを強制終了するのだ。");
                try { process.Kill(true); } catch (InvalidOperationException) { /* プロセスは既に終了しています */ }
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Instagram スクレイピングがタイムアウトしました");
        }

        if (process.ExitCode == 2)
        {
            throw new InvalidOperationException(
                "instaloader パッケージがインストールされていません。アプリケーションを再起動してください。");
        }

        if (process.ExitCode == 3)
        {
            // セッション切れ: セッション関連ファイルを削除
            try
            {
                if (Directory.Exists(sessionDir))
                    Directory.Delete(sessionDir, true);
            }
            catch { /* 削除失敗は無視 */ }
            throw new InvalidOperationException(
                "Instagramのセッションが無効です。再度実行するとブラウザが開くので、ログインしてください。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Instagramスクレイピングスクリプトがエラーで終了しました (終了コード: {process.ExitCode})");
        }

        _logMessage($"Instagram スクレイピング完了なのだ: @{username}");
    }

    /// <summary>
    /// パッケージがインストールされているかチェックする（ProcessHelper 使用）
    /// </summary>
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

        var (exitCode, _, _) = await ProcessUtils.RunAsync(
            startInfo, TimeoutSettings.PythonVersionCheckTimeoutMs, ct);
        return exitCode == 0;
    }

    /// <summary>
    /// pip でパッケージをインストール/更新する（ProcessHelper 使用）
    /// </summary>
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
        startInfo.ArgumentList.Add("--upgrade");
        startInfo.ArgumentList.Add(packageName);

        var (exitCode, output, error) = await ProcessUtils.RunAsync(
            startInfo, TimeoutSettings.PackageInstallTimeoutMs, ct);

        if (!string.IsNullOrEmpty(output))
            _logMessage($"pip: {output.TrimEnd()}");
        if (!string.IsNullOrEmpty(error) && exitCode != 0)
            _logError($"pip エラー: {error.TrimEnd()}");

        if (exitCode == 0)
            _logMessage($"{packageName} のインストール/更新完了なのだ");
        else
            _logError($"{packageName} のインストール/更新に失敗したのだ");
    }

    // ────────────────────────────────────────────
    //  Instagram 進捗解析
    // ────────────────────────────────────────────

    // Instagram 用ログパターン（事前コンパイル）
    private static readonly Regex IgPostProgressPattern = new(
        @"投稿 (\d+):", RegexOptions.Compiled);
    private static readonly Regex IgProgressSummaryPattern = new(
        @"進捗: (\d+)/(\d+) 投稿処理済み", RegexOptions.Compiled);
    private static readonly Regex IgCompletionPattern = new(
        @"=== 完了! 投稿: (\d+)/(\d+), DL: (\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Python スクリプトの stdout ログを解析し、Instagram の進捗表示を更新する
    /// </summary>
    private void UpdateInstagramStatus(string logLine, string username)
    {
        if (_statusCallback is null) return;

        // セッション確認
        if (logLine.Contains("保存済みプロファイルでセッション確認"))
        {
            _statusCallback($"@{username}: セッション確認中...");
            return;
        }
        if (logLine.Contains("セッション復元に成功"))
        {
            _statusCallback($"@{username}: セッション復元完了");
            return;
        }
        if (logLine.Contains("保存済みセッションが有効"))
        {
            _statusCallback($"@{username}: セッション有効！準備中...");
            return;
        }
        if (logLine.Contains("ブラウザが開きました"))
        {
            _statusCallback($"@{username}: ブラウザにログインしてください");
            return;
        }
        if (logLine.Contains("ログイン待機中"))
        {
            _statusCallback($"@{username}: ログイン待機中...");
            return;
        }
        if (logLine.Contains("ログイン完了を検知") || logLine.Contains("ログイン成功"))
        {
            _statusCallback($"@{username}: ログイン成功！準備中...");
            return;
        }

        // プロフィール取得
        if (logLine.Contains("プロフィール取得完了"))
        {
            _statusCallback($"@{username}: プロフィール取得完了！メディアDL開始...");
            return;
        }

        // Phase 2
        if (logLine.Contains("Phase 2: メディアダウンロード"))
        {
            _statusCallback($"@{username}: メディアダウンロード中...");
            return;
        }

        // 進捗サマリー
        var summaryMatch = IgProgressSummaryPattern.Match(logLine);
        if (summaryMatch.Success)
        {
            var current = summaryMatch.Groups[1].Value;
            var total = summaryMatch.Groups[2].Value;
            _statusCallback($"@{username}: メディアDL中... ({current}/{total} 投稿)");
            return;
        }

        // 個別投稿処理
        var postMatch = IgPostProgressPattern.Match(logLine);
        if (postMatch.Success)
        {
            var postNum = postMatch.Groups[1].Value;
            _statusCallback($"@{username}: 投稿 #{postNum} を処理中...");
            return;
        }

        // 完了
        var completionMatch = IgCompletionPattern.Match(logLine);
        if (completionMatch.Success)
        {
            var total = completionMatch.Groups[1].Value;
            var downloaded = completionMatch.Groups[3].Value;
            _statusCallback($"@{username}: 完了！ {total}投稿, {downloaded}件DL");
            return;
        }

        // レートリミット
        if (logLine.Contains("レートリミット検出"))
        {
            _statusCallback($"@{username}: レートリミット待機中...");
            return;
        }

        // メタデータ削除
        if (logLine.Contains("メタデータファイル"))
        {
            _statusCallback($"@{username}: クリーンアップ中...");
            return;
        }
    }

    // ────────────────────────────────────────────
    //  X/Twitter 進捗解析
    // ────────────────────────────────────────────

    // Python ログのパターン（事前コンパイル）
    private static readonly Regex ScrollPattern = new(
        @"スクロール #(\d+).*取得ツイート合計: (\d+)", RegexOptions.Compiled);
    private static readonly Regex ImageDlPattern = new(
        @"画像DL (\d+)/(\d+): (.+)", RegexOptions.Compiled);
    private static readonly Regex TweetCountPattern = new(
        @"ツイート取得完了: (\d+) 件", RegexOptions.Compiled);
    private static readonly Regex ImageDlStartPattern = new(
        @"画像ダウンロード開始: (\d+) 枚", RegexOptions.Compiled);
    private static readonly Regex ImageDlCompletePattern = new(
        @"画像ダウンロード完了", RegexOptions.Compiled);

    /// <summary>
    /// Python スクリプトの stdout ログを解析し、オーバーレイの進捗表示を更新する
    /// </summary>
    private void UpdateXTwitterStatus(string logLine, string username)
    {
        if (_statusCallback is null) return;

        // フェーズ表示
        if (logLine.Contains("Phase 1: ツイート取得"))
        {
            _statusCallback($"@{username}: ツイートを取得中...");
            return;
        }
        if (logLine.Contains("Phase 2: 画像ダウンロード"))
        {
            _statusCallback($"@{username}: 画像をダウンロード中...");
            return;
        }

        // セッション確認
        if (logLine.Contains("保存済みプロファイルでセッション確認"))
        {
            _statusCallback($"@{username}: セッション確認中...");
            return;
        }
        if (logLine.Contains("ブラウザが開きました"))
        {
            _statusCallback($"@{username}: ブラウザにログインしてください");
            return;
        }
        if (logLine.Contains("ログイン待機中"))
        {
            _statusCallback($"@{username}: ログイン待機中...");
            return;
        }
        if (logLine.Contains("ログイン完了を検知") || logLine.Contains("ログイン成功"))
        {
            _statusCallback($"@{username}: ログイン成功！準備中...");
            return;
        }
        if (logLine.Contains("セッション復元に成功"))
        {
            _statusCallback($"@{username}: セッション復元完了");
            return;
        }

        // スクロール進捗（ツイート取得）
        var scrollMatch = ScrollPattern.Match(logLine);
        if (scrollMatch.Success)
        {
            var scrollNum = scrollMatch.Groups[1].Value;
            var tweetCount = scrollMatch.Groups[2].Value;
            _statusCallback($"@{username}: ツイート取得中... ({tweetCount}件, スクロール#{scrollNum})");
            return;
        }

        // ツイート取得完了
        var tweetCompleteMatch = TweetCountPattern.Match(logLine);
        if (tweetCompleteMatch.Success)
        {
            var count = tweetCompleteMatch.Groups[1].Value;
            _statusCallback($"@{username}: ツイート {count}件 取得完了！");
            return;
        }

        // 画像ダウンロード開始
        var imgStartMatch = ImageDlStartPattern.Match(logLine);
        if (imgStartMatch.Success)
        {
            var total = imgStartMatch.Groups[1].Value;
            _statusCallback($"@{username}: 画像ダウンロード中... (0/{total})");
            return;
        }

        // 画像ダウンロード進捗
        var imgMatch = ImageDlPattern.Match(logLine);
        if (imgMatch.Success)
        {
            var current = imgMatch.Groups[1].Value;
            var total = imgMatch.Groups[2].Value;
            var filename = imgMatch.Groups[3].Value;
            _statusCallback($"@{username}: 画像DL {current}/{total} — {filename}");
            return;
        }

        // 画像ダウンロード完了
        if (ImageDlCompletePattern.IsMatch(logLine))
        {
            _statusCallback($"@{username}: 画像ダウンロード完了！JSON保存中...");
            return;
        }

        // 最終完了
        if (logLine.Contains("=== 完了!"))
        {
            _statusCallback($"@{username}: スクレイピング完了！");
            return;
        }

        // 迂回＋再検索
        if (logLine.Contains("別ページを巡回して再検索"))
        {
            _statusCallback($"@{username}: BOT対策回避中...");
            return;
        }
        if (logLine.Contains("until:") && logLine.Contains("検索を再開"))
        {
            _statusCallback($"@{username}: 再検索中...");
            return;
        }

        // 中間JSON保存
        if (logLine.Contains("中間JSONを保存"))
        {
            _statusCallback($"@{username}: 中間データ保存中...");
            return;
        }

        // ヘッドレス再起動
        if (logLine.Contains("ヘッドレスモードで再起動"))
        {
            _statusCallback($"@{username}: スクレイピング準備中...");
            return;
        }
    }
}
