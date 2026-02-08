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
/// Reddit は JSON API、その他は Playwright + Ollama ガイド型で抽出する。
/// </summary>
public sealed class WebScraperService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _ollamaClient;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Action<string>? _statusCallback;
    private PlaywrightScraperService? _playwrightScraper;
    private string? _ollamaUrl;
    private string? _ollamaModel;

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
        _ollamaClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// Playwright スクレイパーを設定する（Python環境が利用可能になった後に呼び出す）
    /// </summary>
    public void SetPlaywrightScraper(PlaywrightScraperService playwrightScraper)
    {
        _playwrightScraper = playwrightScraper;
    }

    /// <summary>
    /// Ollama接続情報を設定する（JSON整形に使用）
    /// </summary>
    public void SetOllamaConfig(string ollamaUrl, string ollamaModel)
    {
        _ollamaUrl = ollamaUrl;
        _ollamaModel = ollamaModel;
    }

    // ────────────────────────────────────────────
    //  公開 API
    // ────────────────────────────────────────────

    /// <summary>
    /// 任意のURLからページ情報を抽出し、JSONファイルとして保存する。
    /// Reddit は JSON API で取得、その他は Playwright + Ollama ガイド型で
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
            var result = await ScrapeRedditAsync(url, ct);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(result, options);
            await File.WriteAllTextAsync(outputPath, json, System.Text.Encoding.UTF8, ct);
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
            var outputDir = Path.GetDirectoryName(outputPath)!;
            _statusCallback?.Invoke($"X.com (@{username}) のスクレイピング中...");
            _logMessage($"X.com 専用スクレイピングを開始するのだ: @{username}");
            await _playwrightScraper.ScrapeXTwitterAsync(username, outputDir, ct);

            // X.comはデータが膨大になるためOllama整形をスキップ
            _statusCallback?.Invoke("スクレイピング完了");
            return;
        }
        else
        {
            if (_playwrightScraper is null)
            {
                throw new InvalidOperationException("Playwright スクレイパーが初期化されていません。");
            }

            // Playwright + Ollama ガイド型でスクレイピング
            _statusCallback?.Invoke("Playwright でスクレイピング中...");
            _logMessage("Playwright + Ollama ガイド型でスクレイピングするのだ...");
            await _playwrightScraper.ScrapeWithBrowserAsync(url, outputPath, ct);
        }

        // Ollama で JSON を整形する
        await FormatJsonWithOllamaAsync(outputPath, ct);

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
    //  Ollama JSON整形
    // ────────────────────────────────────────────

    /// <summary>
    /// Ollama を使用してスクレイピング結果のJSONを整形する。
    /// 情報を欠落させずに、整った構造のJSONに変換する。
    /// 大きなJSONはチャンク分割して処理する。
    /// </summary>
    private async Task FormatJsonWithOllamaAsync(string jsonFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ollamaUrl) || string.IsNullOrEmpty(_ollamaModel))
        {
            _logMessage("Ollama が設定されていないため、JSON整形をスキップするのだ。");
            return;
        }

        if (!File.Exists(jsonFilePath))
        {
            return;
        }

        try
        {
            _statusCallback?.Invoke("Ollama でJSON整形中...");
            _logMessage("Ollama でJSON整形を開始するのだ...");
            var rawJson = await File.ReadAllTextAsync(jsonFilePath, System.Text.Encoding.UTF8, ct);

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                _logMessage("JSONファイルが空のため、整形をスキップするのだ。");
                return;
            }

            // JSONのサイズに応じて処理を分岐
            // Ollamaのコンテキストウィンドウを考慮し、約30KB以下なら一括処理
            const int chunkThreshold = 30_000;

            string? formattedJson;
            if (rawJson.Length <= chunkThreshold)
            {
                formattedJson = await FormatJsonChunkWithOllamaAsync(rawJson, ct);
            }
            else
            {
                formattedJson = await FormatLargeJsonWithOllamaAsync(rawJson, ct);
            }

            if (!string.IsNullOrWhiteSpace(formattedJson))
            {
                // 整形結果がJSON として有効かチェック
                try
                {
                    JsonSerializer.Deserialize<JsonElement>(formattedJson);
                    await File.WriteAllTextAsync(jsonFilePath, formattedJson, System.Text.Encoding.UTF8, ct);
                    _logMessage("Ollama によるJSON整形が完了したのだ！");
                }
                catch (JsonException)
                {
                    _logMessage("Ollama の出力が有効なJSONではないため、元のJSONを保持するのだ。");
                }
            }
            else
            {
                _logMessage("Ollama からの応答が空のため、元のJSONを保持するのだ。");
            }
        }
        catch (HttpRequestException ex)
        {
            _logError($"Ollama への接続に失敗したのだ: {ex.Message}");
            _logMessage("元のJSONをそのまま保持するのだ。");
        }
        catch (TaskCanceledException)
        {
            _logMessage("Ollama のJSON整形がタイムアウトしたのだ。元のJSONを保持するのだ。");
        }
        catch (Exception ex)
        {
            _logError($"JSON整形中にエラーが発生したのだ: {ex.Message}");
            _logMessage("元のJSONをそのまま保持するのだ。");
        }
    }

    /// <summary>
    /// 単一チャンクのJSONをOllamaで整形する
    /// </summary>
    private async Task<string?> FormatJsonChunkWithOllamaAsync(string rawJson, CancellationToken ct)
    {
        return await CallOllamaChatAsync(FormatSystemPrompt, BuildFormatUserPrompt(rawJson), ct);
    }

    /// <summary>
    /// 大きなJSONをチャンク分割してOllamaで整形する。
    /// トップレベルの構造を維持しつつ、ページ単位で分割処理する。
    /// </summary>
    private async Task<string?> FormatLargeJsonWithOllamaAsync(string rawJson, CancellationToken ct)
    {
        _statusCallback?.Invoke("JSONをチャンク分割で整形中...");
        _logMessage("JSONが大きいため、チャンク分割で整形するのだ...");

        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(rawJson);

            // 複数ページ構造の場合、ページごとに分割処理
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array)
            {
                return await FormatMultiPageJsonAsync(root, pages, ct);
            }

            // 単一オブジェクトの場合、content配列を分割して処理
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                return await FormatSinglePageLargeJsonAsync(root, content, ct);
            }

            // その他の場合は分割せずにそのまま送信（サイズ制限超えでも試行）
            _logMessage("JSONの構造が分割に適していないため、一括で整形を試みるのだ...");
            return await FormatJsonChunkWithOllamaAsync(rawJson, ct);
        }
        catch (JsonException)
        {
            _logError("JSON解析エラーのため、一括で整形を試みるのだ...");
            return await FormatJsonChunkWithOllamaAsync(rawJson, ct);
        }
    }

    /// <summary>
    /// 複数ページ構造のJSONを整形する（ページ単位で分割）
    /// </summary>
    private async Task<string?> FormatMultiPageJsonAsync(
        JsonElement root, JsonElement pages, CancellationToken ct)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var formattedPages = new List<JsonElement>();
        var pageCount = pages.GetArrayLength();

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            _statusCallback?.Invoke($"ページ {i + 1}/{pageCount} を整形中...");
            _logMessage($"ページ {i + 1}/{pageCount} を整形中なのだ...");

            var pageJson = JsonSerializer.Serialize(pages[i], jsonOptions);
            var formatted = await FormatJsonChunkWithOllamaAsync(pageJson, ct);

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    formattedPages.Add(JsonSerializer.Deserialize<JsonElement>(formatted));
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
        var resultDict = new Dictionary<string, object>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "pages")
            {
                resultDict["pages"] = formattedPages;
            }
            else
            {
                resultDict[prop.Name] = prop.Value;
            }
        }

        return JsonSerializer.Serialize(resultDict, jsonOptions);
    }

    /// <summary>
    /// 単一ページJSONのcontent配列を要素ごとに分割して整形する
    /// </summary>
    private async Task<string?> FormatSinglePageLargeJsonAsync(
        JsonElement root, JsonElement contentArray, CancellationToken ct)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var formattedItems = new List<JsonElement>();
        var itemCount = contentArray.GetArrayLength();

        for (var i = 0; i < itemCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            _statusCallback?.Invoke($"content要素 {i + 1}/{itemCount} を整形中...");
            _logMessage($"content要素 {i + 1}/{itemCount} を整形中なのだ...");

            var itemJson = JsonSerializer.Serialize(contentArray[i], jsonOptions);
            var formatted = await FormatJsonChunkWithOllamaAsync(itemJson, ct);

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    formattedItems.Add(JsonSerializer.Deserialize<JsonElement>(formatted));
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
        var resultDict = new Dictionary<string, object>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "content")
            {
                resultDict["content"] = formattedItems;
            }
            else
            {
                resultDict[prop.Name] = prop.Value;
            }
        }

        return JsonSerializer.Serialize(resultDict, jsonOptions);
    }

    /// <summary>
    /// Ollama の OpenAI互換チャットエンドポイント (/v1/chat/completions) を呼び出す
    /// </summary>
    private async Task<string?> CallOllamaChatAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var requestBody = new
        {
            model = _ollamaModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            stream = false,
            temperature = 0.1,
            max_tokens = 16384
        };

        var requestJson = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

        var response = await _ollamaClient.PostAsync($"{_ollamaUrl}/v1/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var responseDoc = JsonSerializer.Deserialize<JsonElement>(responseJson);

        // OpenAI互換レスポンス: choices[0].message.content
        if (responseDoc.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var responseText))
        {
            var text = responseText.GetString() ?? "";

            // Ollama の応答から JSON 部分だけを抽出する
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

            // 先に出現する開始文字を特定し、対応する終了文字を決定
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

            // スタックで対応する閉じ文字を探す（文字列リテラル内を考慮）
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

        return null;
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

    // ────────────────────────────────────────────
    //  サイト種別判定
    // ────────────────────────────────────────────

    private enum SiteType { Reddit, XTwitter, Generic }

    private static SiteType DetectSiteType(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (IsRedditHost(host))
                return SiteType.Reddit;
            if (IsXTwitterHost(host) && IsXTwitterUserUrl(uri))
                return SiteType.XTwitter;
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

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        _logMessage($"APIレスポンス取得完了なのだ (サイズ: {body.Length:#,0} bytes)");

        var root = JsonSerializer.Deserialize<JsonElement>(body);
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
        _ollamaClient.Dispose();
    }
}
