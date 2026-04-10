using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LlmChamber;
using MarkItDown.GUI.Models;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Webページをスクレイピングし、JSONで出力するサービス。
/// Reddit は JSON API、その他は Playwright + LLM ガイド型で抽出する。
/// </summary>
public sealed class WebScraperService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Action<string>? _statusCallback;
    private PlaywrightScraperService? _playwrightScraper;
    private ILocalLlm? _llm;

    public WebScraperService(Action<string> logMessage, Action<string>? statusCallback = null, Action<string>? logError = null)
    {
        _statusCallback = statusCallback;
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Playwright スクレイパーを設定する（Python環境が利用可能になった後に呼び出す）
    /// </summary>
    public void SetPlaywrightScraper(PlaywrightScraperService playwrightScraper)
    {
        _playwrightScraper = playwrightScraper;
    }

    /// <summary>
    /// 設定済みの Playwright スクレイパーを取得する
    /// </summary>
    public PlaywrightScraperService? GetPlaywrightScraper() => _playwrightScraper;

    /// <summary>
    /// ローカルLLM インスタンスを設定する（JSON整形に使用）
    /// </summary>
    public void SetLlm(ILocalLlm llm)
    {
        _llm = llm;
    }

    // ────────────────────────────────────────────
    //  公開 API
    // ────────────────────────────────────────────

    /// <summary>
    /// 任意のURLからページ情報を抽出し、JSONファイルとして保存する。
    /// Reddit は JSON API で取得、その他は Playwright + LLM ガイド型で
    /// 動的コンテンツ（ページネーション・もっと見る等）を含めて取得する。
    /// </summary>
    public async Task ScrapeAsync(string url, string outputPath, CancellationToken ct = default)
    {
        url = NormalizeUrl(url);
        var siteType = DetectSiteType(url);
        _logMessage($"サイト種別: {siteType} なのだ");

        if (siteType == SiteType.Reddit)
        {
            // Reddit は JSON API で取得 → C# 側で処理
            _statusCallback?.Invoke("Reddit API からデータを取得中...");
            var result = await ScrapeRedditAsync(url, ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, AppJsonIndentedContext.Default.RedditThreadData);
            await File.WriteAllTextAsync(outputPath, json, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
            _logMessage($"JSONファイルを出力したのだ: {outputPath}");
        }
        else if (siteType == SiteType.RedditSubreddit)
        {
            // Reddit コミュニティ一覧: Hot投稿(最大100件)+全コメントを取得
            _statusCallback?.Invoke("Reddit コミュニティからデータを取得中...");
            var result = await ScrapeRedditSubredditAsync(url, ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, AppJsonIndentedContext.Default.RedditSubredditData);
            await File.WriteAllTextAsync(outputPath, json, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
            _logMessage($"JSONファイルを出力したのだ: {outputPath}");
        }
        else if (siteType == SiteType.XTwitter)
        {
            // X/Twitter はPlaywright専用スクリプトで処理
            if (_playwrightScraper is null)
            {
                throw new InvalidOperationException("Playwright スクレイパーが初期化されていません。");
            }

            var username = ExtractXTwitterUsername(url);
            // outputPath はX/Twitterの場合ディレクトリパスそのものが渡される
            var outputDir = Directory.Exists(outputPath) ? outputPath : Path.GetDirectoryName(outputPath);
            if (outputDir is null)
            {
                _logError($"出力ディレクトリの取得に失敗したのだ: {outputPath}");
                return;
            }
            _statusCallback?.Invoke($"X.com (@{username}) のスクレイピング中...");
            _logMessage($"X.com 専用スクレイピングを開始するのだ: @{username}");
            await _playwrightScraper.ScrapeXTwitterAsync(username, outputDir, ct).ConfigureAwait(false);

            // X.comはデータが膨大になるためLLM整形をスキップ
            _statusCallback?.Invoke("スクレイピング完了");
            return;
        }
        else if (siteType == SiteType.Instagram)
        {
            // Instagram はInstaloader + Playwright認証で処理
            if (_playwrightScraper is null)
            {
                throw new InvalidOperationException("Playwright スクレイパーが初期化されていません。");
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir is null)
            {
                _logError($"出力ディレクトリの取得に失敗したのだ: {outputPath}");
                return;
            }

            // ユーザーURLか投稿/リールURLかを判定
            string target;
            if (TryExtractInstagramUsername(url, out var igUser))
            {
                target = igUser;
                _statusCallback?.Invoke($"Instagram (@{target}) のスクレイピング中...");
                _logMessage($"Instagram 専用スクレイピングを開始するのだ: @{target}");
            }
            else
            {
                // 投稿/リールURL: shortcodeまたはURL全体をPythonに渡す
                target = url;
                _statusCallback?.Invoke("Instagram 投稿のスクレイピング中...");
                _logMessage($"Instagram 投稿/リールのスクレイピングを開始するのだ: {url}");
            }
            await _playwrightScraper.ScrapeInstagramAsync(target, outputDir, ct).ConfigureAwait(false);

            // Instagram はメディアファイルのみダウンロードのためLLM整形不要
            _statusCallback?.Invoke("スクレイピング完了");
            return;
        }
        else
        {
            if (_playwrightScraper is null)
            {
                throw new InvalidOperationException("スクレイパーが初期化されていません。");
            }

            // HTTP + LLM ガイド型でスクレイピング（ブラウザ不要）
            _statusCallback?.Invoke("HTTP でスクレイピング中...");
            _logMessage("HTTP + LLM ガイド型でスクレイピングするのだ（ブラウザ不使用）...");
            await _playwrightScraper.ScrapeWithHttpAsync(url, outputPath, ct).ConfigureAwait(false);
        }

        // LLM で JSON を整形・まとめし、3ファイル出力する
        await ProcessJsonWithLlmAsync(outputPath, ct).ConfigureAwait(false);

        _statusCallback?.Invoke("スクレイピング完了");
    }

    /// <summary>
    /// URLから安全なファイル名を生成する
    /// </summary>
    public static string GenerateSafeFileName(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return FallbackFileName();

            var host = uri.Host.ToLowerInvariant()
                .Replace("www.", "")
                .Replace(".", "_");

            // Reddit スレッド
            var redditThreadMatch = Regex.Match(uri.AbsolutePath,
                @"/r/([^/]+)/comments/([^/]+)(?:/([^/]*))?", RegexOptions.IgnoreCase);
            if (redditThreadMatch.Success)
            {
                var sub = redditThreadMatch.Groups[1].Value;
                var id = redditThreadMatch.Groups[2].Value;
                var slug = redditThreadMatch.Groups[3].Success ? redditThreadMatch.Groups[3].Value : "";
                var name = string.IsNullOrEmpty(slug)
                    ? $"reddit_{sub}_{id}"
                    : $"reddit_{sub}_{id}_{slug}";
                return Sanitize(name) + ".json";
            }

            // Reddit サブレディット（コミュニティTOP）
            var redditSubMatch = Regex.Match(uri.AbsolutePath,
                @"^/r/([^/]+)/?", RegexOptions.IgnoreCase);
            if (redditSubMatch.Success && IsRedditHost(uri.Host.ToLowerInvariant()))
            {
                return Sanitize($"reddit_{redditSubMatch.Groups[1].Value}") + ".json";
            }

            // Instagram — /username
            if (IsInstagramHost(uri.Host.ToLowerInvariant()))
            {
                var igPath = uri.AbsolutePath.Trim('/');
                if (!string.IsNullOrEmpty(igPath))
                {
                    var igUser = igPath.Split('/')[0];
                    return Sanitize($"instagram_{igUser}") + ".json";
                }
            }

            // Amazon — /dp/ASIN
            var amazonMatch = Regex.Match(uri.AbsolutePath,
                @"/dp/([A-Z0-9]{10})", RegexOptions.IgnoreCase);
            if (amazonMatch.Success)
            {
                return Sanitize($"amazon_{amazonMatch.Groups[1].Value}") + ".json";
            }

            // 汎用 — ホスト名 + パスの先頭部分
            var pathPart = uri.AbsolutePath.Trim('/');
            if (pathPart.Length > 80) pathPart = pathPart[..80];
            pathPart = Regex.Replace(pathPart, @"[/\\]", "_");
            var generic = string.IsNullOrEmpty(pathPart) ? host : $"{host}_{pathPart}";
            return Sanitize(generic) + ".json";
        }
        catch
        {
            return FallbackFileName();
        }

        static string FallbackFileName() => $"scraped_{DateTime.Now:yyyyMMdd_HHmmss}.json";

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            // 長すぎるファイル名を制限
            if (name.Length > 120) name = name[..120];
            return name;
        }
    }

    // ────────────────────────────────────────────
    //  LLM JSON整形
    // ────────────────────────────────────────────

    /// <summary>
    /// スクレイピング結果のJSONを2種類のファイルに分けて出力する。
    /// 元データ（生JSON）、まとめ済（LLMによるMarkdownまとめ）。
    /// LLMが利用できない場合は元データのリネームのみ行う。
    /// </summary>
    private async Task ProcessJsonWithLlmAsync(string jsonFilePath, CancellationToken ct)
    {
        if (!File.Exists(jsonFilePath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(jsonFilePath);
        if (dir is null)
        {
            _logError($"出力ディレクトリの取得に失敗したのだ: {jsonFilePath}");
            return;
        }
        var nameWithoutExt = Path.GetFileNameWithoutExtension(jsonFilePath);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

        // 1. 元データ: ファイルをリネーム
        var originPath = Path.Combine(dir, $"{nameWithoutExt}_元データ_{timestamp}.json");
        File.Move(jsonFilePath, originPath);
        _logMessage($"元データ出力完了なのだ: {originPath}");

        if (_llm is null)
        {
            _logMessage("LLM が設定されていないため、まとめをスキップするのだ。");
            return;
        }

        // LLMがまだ初期化中なら完了を待つ
        if (!_llm.IsReady)
        {
            _logMessage("LLM がまだ準備中なのだ。初期化完了を待機中...");
            _statusCallback?.Invoke("LLM の初期化完了を待機中...");
            await _llm.InitializeAsync(ct).ConfigureAwait(false);

            if (!_llm.IsReady)
            {
                _logMessage("LLM の初期化に失敗したのだ。まとめをスキップするのだ。");
                return;
            }
            _logMessage("LLM の初期化が完了したのだ！");
        }

        try
        {
            var rawJson = await File.ReadAllTextAsync(originPath, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                _logMessage("JSONファイルが空のため、まとめをスキップするのだ。");
                return;
            }

            // 2. まとめ済: LLM でMarkdownまとめ
            _statusCallback?.Invoke("LLM でJSONまとめ中...");
            _logMessage("LLM でJSONまとめを開始するのだ...");

            var summaryMd = await SummarizeJsonWithLlmAsync(rawJson, ct).ConfigureAwait(false);
            await WriteLlmOutputAsync(summaryMd, dir, nameWithoutExt, timestamp, "まとめ済", "md", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logMessage("ユーザーによりキャンセルされたのだ。元データのみ保持するのだ。");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logError($"LLM への接続に失敗したのだ: {ex.Message}");
            _logMessage("元データのみ保持するのだ。");
        }
        catch (OperationCanceledException)
        {
            _logMessage("LLM の処理がタイムアウトしたのだ。元データのみ保持するのだ。");
        }
        catch (Exception ex)
        {
            _logError($"JSONまとめ中にエラーが発生したのだ: {ex.Message}");
            _logMessage("元データのみ保持するのだ。");
        }
    }

    /// <summary>
    /// LLM の出力をファイルに書き込む共通ヘルパー。
    /// content が空の場合はスキップし、書き込んだパスを返す（スキップ時は null）。
    /// </summary>
    private async Task<string?> WriteLlmOutputAsync(
        string? content, string directory, string baseName, string timestamp,
        string suffix, string extension, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logMessage($"LLM からの{suffix}応答が空のため、{suffix}ファイルの出力をスキップするのだ。");
            return null;
        }

        var path = Path.Combine(directory, $"{baseName}_{suffix}_{timestamp}.{extension}");
        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
        _logMessage($"{suffix}出力完了なのだ: {path}");
        return path;
    }

    /// <summary>
    /// ローカルLLM を呼び出す
    /// </summary>
    private async Task<string?> CallLlmAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        if (_llm is null || !_llm.IsReady)
            return null;

        var prompt = $"{systemPrompt}\n\n{userMessage}";
        var text = await _llm.GenerateCompleteAsync(prompt, new InferenceOptions
        {
            Temperature = 0.1f,
            MaxTokens = 8192
        }, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // LLM の応答から JSON 部分だけを抽出する
        // ```json ... ``` で囲まれている場合
        var codeBlockMatch = Regex.Match(text, @"```(?:json)?\s*\n?(.*?)\n?```",
            RegexOptions.Singleline);
        if (codeBlockMatch.Success)
        {
            return codeBlockMatch.Groups[1].Value.Trim();
        }

        // JSON オブジェクトまたは配列をスタックベースで抽出
        var objStart = text.IndexOf('{');
        var arrStart = text.IndexOf('[');

        if (objStart == -1 && arrStart == -1)
        {
            return text.Trim();
        }

        int start;
        char openChar;
        char closeChar;

        if (objStart != -1 && (arrStart == -1 || objStart < arrStart))
        {
            start = objStart;
            openChar = '{';
            closeChar = '}';
        }
        else
        {
            start = arrStart;
            openChar = '[';
            closeChar = ']';
        }

        var depth = 0;
        var inString = false;
        var escapeNext = false;
        var end = -1;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escapeNext) { escapeNext = false; continue; }
            if (c == '\\' && inString) { escapeNext = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }

        if (end > start)
        {
            return text[start..(end + 1)];
        }

        return text.Trim();
    }

    /// <summary>
    /// JSON分析・統計まとめ用のシステムプロンプト
    /// </summary>
    private const string SummarySystemPrompt =
        "あなたは日本語だけで回答するアシスタントです。英語では絶対に回答しません。";

    /// <summary>
    /// LLM を使用してJSONデータを分析・統計まとめする
    /// </summary>
    /// <summary>
    /// 日本語まとめ指示テンプレート。ユーザーメッセージに埋め込んでモデルを日本語出力に強制する。
    /// </summary>
    private const string JapaneseSummaryInstruction = """
        【言語指定】日本語で回答してください。English output is forbidden.

        以下のデータを分析し、日本語のMarkdownで出力してください。

        出力テンプレート:
        # （日本語のタイトル）
        ## 概要
        （3〜5文で具体的に説明）
        ## 統計情報
        | 項目 | 値 |
        |---|---|
        | 投稿数 | 数字 |
        | コメント数 | 数字 |
        ## 主要な投稿・話題
        - **（タイトルを日本語訳）**（投稿者名）: 要点を日本語で説明
        ## キーワード
        - 固有名詞、技術用語
        ## まとめ
        （傾向・結論を日本語で記述）

        英語の投稿タイトルやコメントはすべて日本語に翻訳すること。
        固有名詞（Claude, Reddit, GPT等）はそのまま使用可。
        """;

    private async Task<string?> SummarizeJsonWithLlmAsync(string rawJson, CancellationToken ct)
    {
        const int chunkSize = 80_000; // Gemma 4 E4B のコンテキスト（128K）に余裕を持たせる

        // 1チャンクに収まる場合はそのまま処理
        if (rawJson.Length <= chunkSize)
        {
            var userMessage = $"{JapaneseSummaryInstruction}\n\n---\nデータ:\n{rawJson}\n---\n\n上記データの日本語まとめをMarkdownで出力してください。";
            return await CallLlmAsync(SummarySystemPrompt, userMessage, ct).ConfigureAwait(false);
        }

        // チャンク分割 → 各チャンクをまとめ → 最終統合
        _logMessage($"JSONが大きいため、チャンク分割でまとめるのだ（{rawJson.Length:N0}文字）...");
        var chunkSummaries = new List<string>();
        for (var offset = 0; offset < rawJson.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = rawJson.Substring(offset, Math.Min(chunkSize, rawJson.Length - offset));
            var chunkNum = (offset / chunkSize) + 1;
            var totalChunks = (rawJson.Length + chunkSize - 1) / chunkSize;

            _statusCallback?.Invoke($"LLM でまとめ中... ({chunkNum}/{totalChunks})");
            _logMessage($"チャンク {chunkNum}/{totalChunks} をまとめ中なのだ...");

            var chunkMessage = $"{JapaneseSummaryInstruction}\n\n---\nデータ（パート{chunkNum}/{totalChunks}）:\n{chunk}\n---\n\n上記パートの内容を日本語で要約してください。";
            var chunkSummary = await CallLlmAsync(SummarySystemPrompt, chunkMessage, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(chunkSummary))
            {
                chunkSummaries.Add(chunkSummary);
            }
        }

        if (chunkSummaries.Count == 0)
        {
            return null;
        }

        // 最終統合
        if (chunkSummaries.Count == 1)
        {
            return chunkSummaries[0];
        }

        _statusCallback?.Invoke("LLM で最終まとめを統合中...");
        _logMessage("各チャンクのまとめを統合するのだ...");
        var combined = string.Join("\n\n---\n\n", chunkSummaries);
        var finalMessage = $"{JapaneseSummaryInstruction}\n\n---\n以下は同じデータの各パートの要約です:\n{combined}\n---\n\n上記の要約を統合して、1つの包括的な日本語のまとめをMarkdownで出力してください。";
        return await CallLlmAsync(SummarySystemPrompt, finalMessage, ct).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────
    //  サイト種別判定
    // ────────────────────────────────────────────

    private enum SiteType { Reddit, RedditSubreddit, XTwitter, Instagram, Generic }

    private static SiteType DetectSiteType(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (IsRedditHost(host))
            {
                // /r/{sub}/comments/ パターンなら個別スレッド、それ以外はサブレディット一覧
                if (Regex.IsMatch(uri.AbsolutePath, @"/r/[^/]+/comments/", RegexOptions.IgnoreCase))
                    return SiteType.Reddit;
                if (Regex.IsMatch(uri.AbsolutePath, @"^/r/[^/]+/?", RegexOptions.IgnoreCase))
                    return SiteType.RedditSubreddit;
                return SiteType.Reddit;
            }
            if (IsXTwitterHost(host) && IsXTwitterUserUrl(uri))
                return SiteType.XTwitter;
            if (IsInstagramHost(host) && IsInstagramUrl(uri))
                return SiteType.Instagram;
        }
        return SiteType.Generic;
    }

    private static bool IsRedditHost(string host)
    {
        return host is "reddit.com" or "www.reddit.com"
            or "old.reddit.com" or "new.reddit.com"
            or "np.reddit.com" or "m.reddit.com"
            || host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsXTwitterHost(string host)
    {
        return host is "x.com" or "www.x.com"
            or "twitter.com" or "www.twitter.com"
            or "mobile.twitter.com" or "mobile.x.com";
    }

    private static bool IsInstagramHost(string host)
    {
        return host is "instagram.com" or "www.instagram.com"
            or "m.instagram.com"
            || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// InstagramのURL（ユーザーページ、投稿、リール等）かどうかを判定する。
    /// Instagramホストの有効なページURLであれば true を返す。
    /// </summary>
    private static bool IsInstagramUrl(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
            return false;

        var firstSegment = path.Split('/')[0].ToLowerInvariant();

        // Instagram の投稿やリール等もサポート
        var instagramContentPaths = new[] { "p", "reel", "reels", "stories" };
        foreach (var contentPath in instagramContentPaths)
        {
            if (firstSegment == contentPath)
                return true;
        }

        // システムパスを除外
        var excludedPaths = new[]
        {
            "explore", "accounts",
            "direct", "directory", "about", "legal", "privacy",
            "terms", "developer", "api", "static", "challenge",
            "emails", "press", "blog"
        };

        foreach (var excluded in excludedPaths)
        {
            if (firstSegment == excluded)
                return false;
        }

        // ユーザー名はアルファベット、数字、ピリオド、アンダースコア（1〜30文字）
        return Regex.IsMatch(firstSegment, @"^[A-Za-z0-9._]{1,30}$");
    }

    /// <summary>
    /// InstagramのユーザーページURLかどうかを判定する。
    /// /username 形式であり、/p/, /reel/ 等のコンテンツパスやシステムパスではないことを確認する。
    /// </summary>
    private static bool IsInstagramUserUrl(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
            return false;

        // システムパスおよびコンテンツパスを除外
        var excludedPaths = new[]
        {
            "explore", "accounts", "p", "reel", "reels", "stories",
            "direct", "directory", "about", "legal", "privacy",
            "terms", "developer", "api", "static", "challenge",
            "emails", "press", "blog"
        };

        var firstSegment = path.Split('/')[0].ToLowerInvariant();

        foreach (var excluded in excludedPaths)
        {
            if (firstSegment == excluded)
                return false;
        }

        // ユーザー名はアルファベット、数字、ピリオド、アンダースコア（1〜30文字）
        return Regex.IsMatch(firstSegment, @"^[A-Za-z0-9._]{1,30}$");
    }

    /// <summary>
    /// X/TwitterのユーザーページURLかどうかを判定する。
    /// /username 形式であり、/home, /search, /settings, /i/, /explore 等の
    /// システムパスではないことを確認する。
    /// </summary>
    private static bool IsXTwitterUserUrl(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
            return false;

        // システムパスを除外
        var excludedPaths = new[]
        {
            "home", "search", "explore", "notifications", "messages",
            "settings", "i", "compose", "intent", "tos", "privacy",
            "login", "signup", "logout", "about", "help"
        };

        // パスの最初のセグメントを取得
        var firstSegment = path.Split('/')[0].ToLowerInvariant();

        foreach (var excluded in excludedPaths)
        {
            if (firstSegment == excluded)
                return false;
        }

        // ユーザー名は英数字とアンダースコアのみ（1〜15文字）
        return Regex.IsMatch(firstSegment, @"^[A-Za-z0-9_]{1,15}$");
    }

    /// <summary>
    /// X/TwitterのURLからユーザー名を抽出する
    /// </summary>
    public static string ExtractXTwitterUsername(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"無効なURL: {url}");

        var path = uri.AbsolutePath.Trim('/');
        var username = path.Split('/')[0];

        // クエリパラメータ等を除去
        if (username.Contains('?'))
            username = username.Split('?')[0];

        return username;
    }

    /// <summary>
    /// X/TwitterのユーザーページURLであればユーザー名を取得する
    /// </summary>
    public static bool TryExtractXTwitterUsername(string url, out string username)
    {
        username = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsXTwitterUserUrl(uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        username = path.Split('/')[0];
        if (username.Contains('?'))
        {
            username = username.Split('?')[0];
        }

        return !string.IsNullOrWhiteSpace(username);
    }

    /// <summary>
    /// InstagramのURLからユーザー名を抽出する
    /// </summary>
    public static string ExtractInstagramUsername(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"無効なURL: {url}");

        var path = uri.AbsolutePath.Trim('/');
        var username = path.Split('/')[0];

        if (username.Contains('?'))
            username = username.Split('?')[0];

        return username;
    }

    /// <summary>
    /// InstagramのユーザーページURLであればユーザー名を取得する
    /// </summary>
    public static bool TryExtractInstagramUsername(string url, out string username)
    {
        username = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsInstagramUserUrl(uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        username = path.Split('/')[0];
        if (username.Contains('?'))
        {
            username = username.Split('?')[0];
        }

        return !string.IsNullOrWhiteSpace(username);
    }

    // ────────────────────────────────────────────
    //  URL正規化
    // ────────────────────────────────────────────

    private static string NormalizeUrl(string inputUrl)
    {
        inputUrl = inputUrl.Trim();
        if (!inputUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !inputUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            inputUrl = "https://" + inputUrl;
        }
        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out _))
            throw new ArgumentException($"無効なURL: {inputUrl}");
        return inputUrl;
    }

    // ════════════════════════════════════════════
    //  Reddit サブレディット（コミュニティ一覧）スクレイパー
    // ════════════════════════════════════════════

    private const int RedditMaxPosts = 100;  // 親投稿の最大取得件数
    private const int RedditListingLimit = 100;  // Reddit API の1リクエストあたり最大件数
    private const int RedditApiDelayMs = 2000; // リクエスト間ディレイ（ms）— 非認証APIは厳しい制限があるため余裕を持つ
    private const int RedditMaxRetries = 5; // レート制限時の最大リトライ回数

    /// <summary>
    /// サブレディットのHot投稿一覧（最大200件）を取得し、各投稿のコメントを全取得する
    /// </summary>
    private async Task<RedditSubredditData> ScrapeRedditSubredditAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        var subredditMatch = Regex.Match(uri.AbsolutePath, @"/r/([^/]+)", RegexOptions.IgnoreCase);
        if (!subredditMatch.Success)
            throw new ArgumentException($"サブレディットのURLパターンに一致しません: {url}");

        var subreddit = subredditMatch.Groups[1].Value;
        _logMessage($"サブレディット r/{subreddit} の投稿一覧を取得するのだ");

        // ── フェーズ1: 投稿一覧をページネーションで取得（Hot固定、最大100件） ──
        var allPosts = new List<RedditPost>();
        string? after = null;

        while (allPosts.Count < RedditMaxPosts)
        {
            ct.ThrowIfCancellationRequested();

            var listingUrl = $"https://www.reddit.com/r/{subreddit}/hot.json?limit={RedditListingLimit}&raw_json=1";
            if (!string.IsNullOrEmpty(after))
                listingUrl += $"&after={after}";

            _statusCallback?.Invoke($"投稿一覧を取得中... ({allPosts.Count} 件取得済み)");
            _logMessage($"投稿一覧APIにアクセス中なのだ: {listingUrl}");

            using var request = new HttpRequestMessage(HttpMethod.Get, listingUrl);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logMessage("レート制限に到達したのだ。10秒待機するのだ...");
                await Task.Delay(10_000, ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize(body, AppJsonContext.Default.JsonElement);

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logMessage("予期しないレスポンス形式なのだ。取得を終了するのだ。");
                break;
            }

            if (!root.TryGetProperty("data", out var data))
                break;

            if (!data.TryGetProperty("children", out var children))
                break;

            var batchCount = 0;
            foreach (var child in children.EnumerateArray())
            {
                if (allPosts.Count >= RedditMaxPosts) break;

                var kind = Str(child, "kind") ?? "";
                if (kind != "t3") continue; // t3 = リンク（投稿）
                if (!child.TryGetProperty("data", out var d)) continue;

                allPosts.Add(new RedditPost
                {
                    Title = Str(d, "title") ?? "",
                    Author = Str(d, "author") ?? "[deleted]",
                    Subreddit = Str(d, "subreddit") ?? subreddit,
                    Score = Int(d, "score") ?? 0,
                    UpvoteRatio = Dbl(d, "upvote_ratio"),
                    CreatedUtc = Dbl(d, "created_utc"),
                    SelfText = Str(d, "selftext") ?? "",
                    Url = Str(d, "url") ?? "",
                    Permalink = Str(d, "permalink") ?? "",
                    NumComments = Int(d, "num_comments") ?? 0,
                    IsVideo = Bool(d, "is_video"),
                    LinkFlairText = Str(d, "link_flair_text"),
                    Domain = Str(d, "domain")
                });
                batchCount++;
            }

            _logMessage($"投稿 {batchCount} 件を取得したのだ (合計: {allPosts.Count} 件)");

            // 次ページの after トークンを取得
            after = Str(data, "after");
            if (string.IsNullOrEmpty(after))
            {
                _logMessage("全投稿を取得完了したのだ");
                break;
            }

            await Task.Delay(RedditApiDelayMs, ct).ConfigureAwait(false);
        }

        _logMessage($"投稿一覧の取得完了なのだ: 合計 {allPosts.Count} 件");

        // ── フェーズ2: 各投稿のコメントをシーケンシャルに取得 ──
        // Reddit 非認証 API はレート制限が厳しい (~10 req/min) ため、
        // 並列ではなく1件ずつ取得し、適応的にディレイを調整する
        var threads = new List<RedditThreadData>();
        var totalComments = 0;
        var processedPosts = 0;
        var currentDelayMs = RedditApiDelayMs; // 適応的ディレイ（レート制限を受けたら増加）
        var consecutiveRateLimits = 0; // 連続レート制限回数

        foreach (var post in allPosts)
        {
            ct.ThrowIfCancellationRequested();

            var comments = new List<RedditComment>();
            var permalink = post.Permalink;

            if (!string.IsNullOrEmpty(permalink))
            {
                try
                {
                    var threadJsonUrl = $"https://www.reddit.com{permalink.TrimEnd('/')}.json?raw_json=1";

                    for (var attempt = 0; attempt <= RedditMaxRetries; attempt++)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, threadJsonUrl);
                        req.Headers.Accept.ParseAdd("application/json");

                        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

                        if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            consecutiveRateLimits++;

                            if (attempt == RedditMaxRetries)
                            {
                                _logMessage($"コメント取得断念 (リトライ上限): {post.Title}");
                                break;
                            }

                            // Retry-After ヘッダがあれば尊重、なければ指数バックオフ
                            var backoffMs = 10_000 * (1 << attempt); // 10s, 20s, 40s, 80s, 160s
                            if (resp.Headers.RetryAfter?.Delta is { } retryDelta)
                            {
                                backoffMs = Math.Max(backoffMs, (int)retryDelta.TotalMilliseconds + 1000);
                            }

                            _logMessage($"レート制限 (リトライ {attempt + 1}/{RedditMaxRetries}, {backoffMs / 1000}秒待機): {post.Title}");
                            await Task.Delay(backoffMs, ct).ConfigureAwait(false);

                            // レート制限を連続で受けたら、通常ディレイも増加させる（最大10秒）
                            if (consecutiveRateLimits >= 2)
                            {
                                currentDelayMs = Math.Min(currentDelayMs + 1000, 10_000);
                                _logMessage($"リクエスト間隔を {currentDelayMs}ms に増加したのだ");
                            }

                            continue;
                        }

                        // 成功した場合、連続レート制限カウンタをリセット
                        consecutiveRateLimits = 0;

                        if (resp.IsSuccessStatusCode)
                        {
                            var threadBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            comments = ParseCommentsFromThreadJson(threadBody);
                        }
                        break;
                    }

                    // 次のリクエストまでディレイ
                    await Task.Delay(currentDelayMs, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logMessage($"コメント取得失敗 (スキップ): {post.Title} - {ex.Message}");
                }
            }

            var commentCount = CountComments(comments);
            threads.Add(new RedditThreadData
            {
                Url = !string.IsNullOrEmpty(post.Permalink)
                    ? $"https://www.reddit.com{post.Permalink}"
                    : post.Url,
                Subreddit = post.Subreddit,
                Post = post,
                Comments = comments,
                ScrapedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });

            totalComments += commentCount;
            processedPosts++;

            if (processedPosts % 10 == 0 || processedPosts == allPosts.Count)
            {
                _statusCallback?.Invoke($"コメント取得中... ({processedPosts}/{allPosts.Count} 投稿, {totalComments} コメント)");
                _logMessage($"コメント取得進捗: {processedPosts}/{allPosts.Count} 投稿, {totalComments} コメント");
            }
        }

        // permalink 順にソート（時系列に近い順序を保持）
        threads.Sort((a, b) => string.Compare(a.Post.Permalink, b.Post.Permalink, StringComparison.Ordinal));

        _logMessage($"コミュニティ取得完了なのだ: {threads.Count} 投稿, {totalComments} コメント");

        return new RedditSubredditData
        {
            Url = url,
            Subreddit = subreddit,
            TotalPosts = threads.Count,
            TotalComments = totalComments,
            Threads = threads,
            ScrapedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }

    /// <summary>
    /// スレッドJSON（配列[投稿, コメント]）からコメント一覧をパースする
    /// </summary>
    private List<RedditComment> ParseCommentsFromThreadJson(string json)
    {
        try
        {
            var root = JsonSerializer.Deserialize(json, AppJsonContext.Default.JsonElement);
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
                return ExtractRedditComments(root[1]);
        }
        catch
        {
            // パース失敗時は空リストを返す
        }
        return [];
    }

    // ════════════════════════════════════════════
    //  Reddit スレッド（個別投稿）スクレイパー
    // ════════════════════════════════════════════

    private async Task<RedditThreadData> ScrapeRedditAsync(string url, CancellationToken ct)
    {
        var jsonUrl = BuildRedditJsonUrl(url);
        _logMessage($"Reddit APIにアクセス中なのだ: {jsonUrl}");

        using var request = new HttpRequestMessage(HttpMethod.Get, jsonUrl);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logMessage($"APIレスポンス取得完了なのだ (サイズ: {body.Length:#,0} bytes)");

        var root = JsonSerializer.Deserialize(body, AppJsonContext.Default.JsonElement);
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
            throw new InvalidOperationException("予期しないReddit APIレスポンス形式です。");

        var post = ExtractRedditPost(root[0]);
        _logMessage($"投稿を抽出したのだ: {post.Title}");

        var comments = ExtractRedditComments(root[1]);
        _logMessage($"コメント数: {CountComments(comments)}件なのだ");

        return new RedditThreadData
        {
            Url = url,
            Subreddit = post.Subreddit,
            Post = post,
            Comments = comments,
            ScrapedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }

    private string BuildRedditJsonUrl(string inputUrl)
    {
        var uri = new Uri(inputUrl);
        var path = uri.AbsolutePath;

        var match = Regex.Match(path, @"/r/([^/]+)/comments/([^/]+)(?:/([^/]*))?", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new ArgumentException($"RedditスレッドのURLパターンに一致しません: {inputUrl}");

        var sub = match.Groups[1].Value;
        var postId = match.Groups[2].Value;
        var slug = match.Groups[3].Success ? match.Groups[3].Value : "";

        var normalizedPath = string.IsNullOrEmpty(slug)
            ? $"/r/{sub}/comments/{postId}"
            : $"/r/{sub}/comments/{postId}/{slug}";

        var jsonUrl = $"https://www.reddit.com{normalizedPath.TrimEnd('/')}.json";
        _logMessage($"正規化URLなのだ: {jsonUrl}");
        return jsonUrl;
    }

    private static RedditPost ExtractRedditPost(JsonElement postListing)
    {
        var d = postListing.GetProperty("data").GetProperty("children")[0].GetProperty("data");
        return new RedditPost
        {
            Title = Str(d, "title") ?? "",
            Author = Str(d, "author") ?? "[deleted]",
            Subreddit = Str(d, "subreddit") ?? "",
            Score = Int(d, "score") ?? 0,
            UpvoteRatio = Dbl(d, "upvote_ratio"),
            CreatedUtc = Dbl(d, "created_utc"),
            SelfText = Str(d, "selftext") ?? "",
            Url = Str(d, "url") ?? "",
            Permalink = Str(d, "permalink") ?? "",
            NumComments = Int(d, "num_comments") ?? 0,
            IsVideo = Bool(d, "is_video"),
            LinkFlairText = Str(d, "link_flair_text"),
            Domain = Str(d, "domain")
        };
    }

    private static List<RedditComment> ExtractRedditComments(JsonElement listing)
    {
        var result = new List<RedditComment>();
        if (listing.ValueKind != JsonValueKind.Object) return result;
        if (!listing.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("children", out var children)) return result;

        foreach (var child in children.EnumerateArray())
        {
            var kind = Str(child, "kind") ?? "";

            if (kind == "more")
            {
                if (child.TryGetProperty("data", out var moreData))
                {
                    var count = Int(moreData, "count") ?? 0;
                    if (count > 0)
                        result.Add(new RedditComment
                        {
                            Author = "[more comments]",
                            Body = $"（{count}件の追加コメントがあります）",
                            Score = 0,
                            Replies = []
                        });
                }
                continue;
            }

            if (kind != "t1") continue;
            if (!child.TryGetProperty("data", out var cd)) continue;

            var comment = new RedditComment
            {
                Author = Str(cd, "author") ?? "[deleted]",
                Body = Str(cd, "body") ?? "",
                Score = Int(cd, "score") ?? 0,
                CreatedUtc = Dbl(cd, "created_utc"),
                IsSubmitter = Bool(cd, "is_submitter"),
                Edited = GetEdited(cd),
                Distinguished = Str(cd, "distinguished"),
                Replies = []
            };

            if (cd.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Object)
                comment.Replies = ExtractRedditComments(replies);

            result.Add(comment);
        }
        return result;
    }

    private static int CountComments(List<RedditComment> list)
    {
        var n = 0;
        foreach (var c in list)
        {
            n++;
            if (c.Replies is { Count: > 0 }) n += CountComments(c.Replies);
        }
        return n;
    }

    // ── JSON ヘルパー ──

    private static object? GetEdited(JsonElement el)
    {
        if (!el.TryGetProperty("edited", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            _ => null
        };
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool? Bool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
