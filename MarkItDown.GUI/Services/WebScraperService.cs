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
using MarkItDown.GUI.Models;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Webページをスクレイピングし、JSONで出力するサービス。
/// Reddit は JSON API、その他は Playwright + Claude ガイド型で抽出する。
/// </summary>
public sealed class WebScraperService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Action<string>? _statusCallback;
    private PlaywrightScraperService? _playwrightScraper;
    private ClaudeCodeProcessHost? _claudeHost;

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
    /// Claude Code CLI 接続情報を設定する（JSON整形に使用）
    /// </summary>
    public void SetClaudeConfig(string nodePath, string cliJsPath)
    {
        _claudeHost = new ClaudeCodeProcessHost();
    }

    // ────────────────────────────────────────────
    //  公開 API
    // ────────────────────────────────────────────

    /// <summary>
    /// 任意のURLからページ情報を抽出し、JSONファイルとして保存する。
    /// Reddit は JSON API で取得、その他は Playwright + Claude ガイド型で
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

            // X.comはデータが膨大になるためClaude整形をスキップ
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

            // Instagram はメディアファイルのみダウンロードのためClaude整形不要
            _statusCallback?.Invoke("スクレイピング完了");
            return;
        }
        else
        {
            if (_playwrightScraper is null)
            {
                throw new InvalidOperationException("スクレイパーが初期化されていません。");
            }

            // HTTP + Claude ガイド型でスクレイピング（ブラウザ不要）
            _statusCallback?.Invoke("HTTP でスクレイピング中...");
            _logMessage("HTTP + Claude ガイド型でスクレイピングするのだ（ブラウザ不使用）...");
            await _playwrightScraper.ScrapeWithHttpAsync(url, outputPath, ct).ConfigureAwait(false);
        }

        // Claude で JSON を整形・まとめし、3ファイル出力する
        await ProcessJsonWithClaudeAsync(outputPath, ct).ConfigureAwait(false);

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

            // Reddit
            var redditMatch = Regex.Match(uri.AbsolutePath,
                @"/r/([^/]+)/comments/([^/]+)(?:/([^/]*))?", RegexOptions.IgnoreCase);
            if (redditMatch.Success)
            {
                var sub = redditMatch.Groups[1].Value;
                var id = redditMatch.Groups[2].Value;
                var slug = redditMatch.Groups[3].Success ? redditMatch.Groups[3].Value : "";
                var name = string.IsNullOrEmpty(slug)
                    ? $"reddit_{sub}_{id}"
                    : $"reddit_{sub}_{id}_{slug}";
                return Sanitize(name) + ".json";
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
    //  Claude JSON整形
    // ────────────────────────────────────────────

    /// <summary>
    /// スクレイピング結果のJSONを3種類のファイルに分けて出力する。
    /// 元データ（生JSON）、整形済（Claude整形JSON）、まとめ済（ClaudeによるMarkdownまとめ）。
    /// Claudeが利用できない場合は元データのリネームのみ行う。
    /// </summary>
    private async Task ProcessJsonWithClaudeAsync(string jsonFilePath, CancellationToken ct)
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

        if (_claudeHost is null || !_claudeHost.IsAvailable())
        {
            _logMessage("Claude が設定されていないため、整形・まとめをスキップするのだ。");
            return;
        }

        try
        {
            var rawJson = await File.ReadAllTextAsync(originPath, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                _logMessage("JSONファイルが空のため、整形・まとめをスキップするのだ。");
                return;
            }

            // 2. 整形済: Claude でJSON整形
            _statusCallback?.Invoke("Claude でJSON整形中...");
            _logMessage("Claude でJSON整形を開始するのだ...");

            const int chunkThreshold = 30_000;
            string? formattedJson;
            if (rawJson.Length <= chunkThreshold)
            {
                formattedJson = await FormatJsonChunkWithClaudeAsync(rawJson, ct).ConfigureAwait(false);
            }
            else
            {
                formattedJson = await FormatLargeJsonWithClaudeAsync(rawJson, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(formattedJson))
            {
                try
                {
                    JsonSerializer.Deserialize(formattedJson, AppJsonContext.Default.JsonElement);
                    await WriteClaudeOutputAsync(formattedJson, dir, nameWithoutExt, timestamp, "整形済", "json", ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    _logMessage("Claude の出力が有効なJSONではないため、整形済ファイルの出力をスキップするのだ。");
                }
            }
            else
            {
                _logMessage("Claude からの応答が空のため、整形済ファイルの出力をスキップするのだ。");
            }

            // 3. まとめ済: Claude でMarkdownまとめ
            _statusCallback?.Invoke("Claude でJSONまとめ中...");
            _logMessage("Claude でJSONまとめを開始するのだ...");

            var summaryMd = await SummarizeJsonWithClaudeAsync(rawJson, ct).ConfigureAwait(false);
            await WriteClaudeOutputAsync(summaryMd, dir, nameWithoutExt, timestamp, "まとめ済", "md", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logMessage("ユーザーによりキャンセルされたのだ。元データのみ保持するのだ。");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logError($"Claude への接続に失敗したのだ: {ex.Message}");
            _logMessage("元データのみ保持するのだ。");
        }
        catch (OperationCanceledException)
        {
            _logMessage("Claude の処理がタイムアウトしたのだ。元データのみ保持するのだ。");
        }
        catch (Exception ex)
        {
            _logError($"JSON整形・まとめ中にエラーが発生したのだ: {ex.Message}");
            _logMessage("元データのみ保持するのだ。");
        }
    }

    /// <summary>
    /// Claude の出力をファイルに書き込む共通ヘルパー。
    /// content が空の場合はスキップし、書き込んだパスを返す（スキップ時は null）。
    /// </summary>
    private async Task<string?> WriteClaudeOutputAsync(
        string? content, string directory, string baseName, string timestamp,
        string suffix, string extension, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logMessage($"Claude からの{suffix}応答が空のため、{suffix}ファイルの出力をスキップするのだ。");
            return null;
        }

        var path = Path.Combine(directory, $"{baseName}_{suffix}_{timestamp}.{extension}");
        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
        _logMessage($"{suffix}出力完了なのだ: {path}");
        return path;
    }

    /// <summary>
    /// 単一チャンクのJSONをClaudeで整形する
    /// </summary>
    private async Task<string?> FormatJsonChunkWithClaudeAsync(string rawJson, CancellationToken ct)
    {
        return await CallClaudeAsync(FormatSystemPrompt, BuildFormatUserPrompt(rawJson), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 大きなJSONをチャンク分割してClaudeで整形する。
    /// トップレベルの構造を維持しつつ、ページ単位で分割処理する。
    /// </summary>
    private async Task<string?> FormatLargeJsonWithClaudeAsync(string rawJson, CancellationToken ct)
    {
        _statusCallback?.Invoke("JSONをチャンク分割で整形中...");
        _logMessage("JSONが大きいため、チャンク分割で整形するのだ...");

        try
        {
            var root = JsonSerializer.Deserialize(rawJson, AppJsonContext.Default.JsonElement);

            // 複数ページ構造の場合、ページごとに分割処理
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array)
            {
                return await FormatMultiPageJsonAsync(root, pages, ct).ConfigureAwait(false);
            }

            // 単一オブジェクトの場合、content配列を分割して処理
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                return await FormatSinglePageLargeJsonAsync(root, content, ct).ConfigureAwait(false);
            }

            // その他の場合は分割せずにそのまま送信（サイズ制限超えでも試行）
            _logMessage("JSONの構造が分割に適していないため、一括で整形を試みるのだ...");
            return await FormatJsonChunkWithClaudeAsync(rawJson, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            _logError("JSON解析エラーのため、一括で整形を試みるのだ...");
            return await FormatJsonChunkWithClaudeAsync(rawJson, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 複数ページ構造のJSONを整形する（ページ単位で分割）
    /// </summary>
    private async Task<string?> FormatMultiPageJsonAsync(
        JsonElement root, JsonElement pages, CancellationToken ct)
    {
        var formattedPages = new List<JsonElement>();
        var pageCount = pages.GetArrayLength();

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            _statusCallback?.Invoke($"ページ {i + 1}/{pageCount} を整形中...");
            _logMessage($"ページ {i + 1}/{pageCount} を整形中なのだ...");

            var pageJson = JsonSerializer.Serialize(pages[i], AppJsonIndentedContext.Default.JsonElement);
            var formatted = await FormatJsonChunkWithClaudeAsync(pageJson, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    formattedPages.Add(JsonSerializer.Deserialize(formatted, AppJsonContext.Default.JsonElement));
                    continue;
                }
                catch (JsonException)
                {
                    _logMessage($"ページ {i + 1} の整形結果が無効なJSONのため、元データを使用するのだ。");
                }
            }
            // 整形失敗時は元のページデータを保持
            formattedPages.Add(pages[i]);
        }

        // トップレベル構造を再構築
        var resultDict = new Dictionary<string, JsonElement>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "pages")
            {
                resultDict["pages"] = JsonSerializer.SerializeToElement(formattedPages, AppJsonContext.Default.ListJsonElement);
            }
            else
            {
                resultDict[prop.Name] = prop.Value;
            }
        }

        return JsonSerializer.Serialize(resultDict, AppJsonIndentedContext.Default.DictionaryStringJsonElement);
    }

    /// <summary>
    /// 単一ページJSONのcontent配列を要素ごとに分割して整形する
    /// </summary>
    private async Task<string?> FormatSinglePageLargeJsonAsync(
        JsonElement root, JsonElement contentArray, CancellationToken ct)
    {
        var formattedItems = new List<JsonElement>();
        var itemCount = contentArray.GetArrayLength();

        for (var i = 0; i < itemCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            _statusCallback?.Invoke($"content要素 {i + 1}/{itemCount} を整形中...");
            _logMessage($"content要素 {i + 1}/{itemCount} を整形中なのだ...");

            var itemJson = JsonSerializer.Serialize(contentArray[i], AppJsonIndentedContext.Default.JsonElement);
            var formatted = await FormatJsonChunkWithClaudeAsync(itemJson, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    formattedItems.Add(JsonSerializer.Deserialize(formatted, AppJsonContext.Default.JsonElement));
                    continue;
                }
                catch (JsonException)
                {
                    _logMessage($"content要素 {i + 1} の整形結果が無効なJSONのため、元データを使用するのだ。");
                }
            }
            // 整形失敗時は元のデータを保持
            formattedItems.Add(contentArray[i]);
        }

        // トップレベル構造を再構築
        var resultDict = new Dictionary<string, JsonElement>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "content")
            {
                resultDict["content"] = JsonSerializer.SerializeToElement(formattedItems, AppJsonContext.Default.ListJsonElement);
            }
            else
            {
                resultDict[prop.Name] = prop.Value;
            }
        }

        return JsonSerializer.Serialize(resultDict, AppJsonIndentedContext.Default.DictionaryStringJsonElement);
    }

    /// <summary>
    /// Claude Code CLI を呼び出す
    /// </summary>
    private async Task<string?> CallClaudeAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        if (_claudeHost is null)
            return null;

        var prompt = $"{systemPrompt}\n\n{userMessage}";
        var text = await _claudeHost.ExecuteAsync(prompt, "", ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Claude の応答から JSON 部分だけを抽出する
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
    /// JSON整形用のシステムプロンプト
    /// </summary>
    private const string FormatSystemPrompt = """
        あなたはJSON整形の専門家です。スクレイピングで取得した生のJSONデータを、
        整った構造の読みやすいJSONに整形してください。

        【重要な規則】
        1. 情報を絶対に欠落させないでください。全てのデータを保持してください。
        2. 出力はJSONのみにしてください。説明文やマークダウンは不要です。
        3. 重複している内容は1つにまとめてください。
        4. テキストコンテンツは意味のある単位でグループ化してください。
        5. 空文字列や意味のないデータ（ナビゲーション要素、広告テキスト等）は除去してください。
        6. 日本語と英語のコンテンツはそのまま保持してください。翻訳しないでください。
        7. URLやリンク情報はそのまま保持してください。
        8. 日時情報はISO 8601形式で保持してください。
        9. ページ番号やメタデータも保持してください。
        """;

    /// <summary>
    /// JSON整形用のユーザーメッセージを構築する
    /// </summary>
    private static string BuildFormatUserPrompt(string rawJson)
    {
        return $"以下のJSONを整形してください。整形されたJSONのみを出力してください:\n\n{rawJson}";
    }

    /// <summary>
    /// JSON分析・統計まとめ用のシステムプロンプト
    /// </summary>
    private const string SummarySystemPrompt = """
        あなたは文書分析の専門家です。
        入力されたJSONデータの内容を分析し、統計情報と構造化されたまとめをMarkdown形式で作成してください。

        【出力構成（この順序で出力すること）】
        1. 概要: データ全体の目的・テーマを2〜3文で説明
        2. 統計情報: レコード数、フィールド数、データ型の分布、ユニーク値の数など該当するものを表形式で列挙
        3. データ構造: JSONの階層構造やキー一覧をツリー形式で表示
        4. 主要トピック: データ内の主要なトピック・カテゴリを箇条書きで列挙し、各トピックの要点を1〜2文で説明
        5. キーワード・固有名詞: データ内で重要なキーワード、固有名詞、数値データを列挙
        6. 結論・要点: データから読み取れる結論や最も重要なポイントをまとめる

        【ルール】
        1. 元のテキストの言語をそのまま使用する（日本語→日本語、英語→英語）
        2. JSONの構造そのものではなく、内容・意味を分析する
        3. 出力はMarkdown形式のみ（JSONではない）
        4. 情報を省略せず、網羅的に分析する
        5. 分析結果のMarkdownだけを出力する（説明文や前置きは不要）
        """;

    /// <summary>
    /// Claude を使用してJSONデータを分析・統計まとめする
    /// </summary>
    private async Task<string?> SummarizeJsonWithClaudeAsync(string rawJson, CancellationToken ct)
    {
        var userMessage = $"以下のJSONデータを分析し、統計情報と構造化されたまとめをMarkdown形式で作成してください:\n\n{rawJson}";
        return await CallClaudeAsync(SummarySystemPrompt, userMessage, ct).ConfigureAwait(false);
    }

    // ────────────────────────────────────────────
    //  サイト種別判定
    // ────────────────────────────────────────────

    private enum SiteType { Reddit, XTwitter, Instagram, Generic }

    private static SiteType DetectSiteType(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (IsRedditHost(host))
                return SiteType.Reddit;
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
    //  Reddit スクレイパー
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
