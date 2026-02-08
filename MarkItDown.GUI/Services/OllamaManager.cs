using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Ollamaとの連携を管理するクラス
/// </summary>
public class OllamaManager : IDisposable
{
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Action<string> _logWarning;
    private readonly Action<double>? _progressCallback;
    private readonly Action<string>? _statusCallback;
    private readonly HttpClient _httpClient;
    private string _ollamaUrl = "http://localhost:11434";
    private const string DefaultModelName = "gemma3:4b";
    private bool _isAvailable;
    private Process? _ollamaProcess;
    private string _ollamaExePath = string.Empty;
    private bool _isExtracting;
    private bool _disposed;
    private string? _cachedTagsJson; // CheckOllamaAvailabilityAsync の成功レスポンスをキャッシュ

    private static readonly HttpClient HttpClientForDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    // ollama pull の進捗パース用（毎行の Regex.Match をプリコンパイルで高速化）
    private static readonly Regex PercentRegex = new(@"(\d+)%", RegexOptions.Compiled);
    private static readonly Regex SizeRegex = new(@"([\d.]+\s*[KMGT]?B)\s*/\s*([\d.]+\s*[KMGT]?B)", RegexOptions.Compiled);

    /// <summary>
    /// Ollamaが利用可能かどうか
    /// </summary>
    public bool IsOllamaAvailable => _isAvailable;

    /// <summary>
    /// OllamaのエンドポイントURL
    /// </summary>
    public string OllamaUrl => _ollamaUrl;

    /// <summary>
    /// 使用するデフォルトのモデル名
    /// </summary>
    public string DefaultModel => DefaultModelName;

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="logMessage">ログ出力関数</param>
    /// <param name="progressCallback">進捗コールバック関数（オプション）</param>
    /// <param name="statusCallback">ステータスメッセージ更新コールバック（オプション）</param>
    public OllamaManager(Action<string> logMessage, Action<double>? progressCallback = null, Action<string>? statusCallback = null, Action<string>? logError = null, Action<string>? logWarning = null)
    {
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        _logWarning = logWarning ?? logMessage;
        _progressCallback = progressCallback;
        _statusCallback = statusCallback;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Ollama環境を初期化する
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logMessage("Ollama環境の初期化を開始するのだ...");

            var savedUrl = AppSettings.GetOllamaUrl();
            if (!string.IsNullOrEmpty(savedUrl))
            {
                _ollamaUrl = savedUrl;
            }

            _logMessage($"Ollama URLなのだ: {_ollamaUrl}");
            _logMessage($"Ollama モデルなのだ: {DefaultModelName}");

            var ollamaBaseDir = Path.Combine(AppPathHelper.LibDirectory, "ollama");
            _logMessage($"Ollamaディレクトリなのだ: {ollamaBaseDir}");

            if (Directory.Exists(ollamaBaseDir))
            {
                var ollamaExe = Path.Combine(ollamaBaseDir, "ollama.exe");
                if (File.Exists(ollamaExe))
                {
                    _ollamaExePath = ollamaExe;
                    _logMessage($"既存のOllamaを検出したのだ: {_ollamaExePath}");
                }
            }

            var needsStartup = false;
            if (string.IsNullOrEmpty(_ollamaExePath))
            {
                _logMessage("埋め込みOllamaが見つからないため、ダウンロードを試行するのだ。");
                _statusCallback?.Invoke("Ollama本体をダウンロード中...");
                if (await DownloadAndExtractOllamaAsync(ollamaBaseDir))
                {
                    _logMessage($"Ollamaの準備が完了したのだ: {_ollamaExePath}");
                    needsStartup = true;
                }
                else
                {
                    _logError("Ollamaのダウンロードに失敗したのだ。画像説明機能は無効になるのだ。");
                    _isAvailable = false;
                    return;
                }
            }

            if (!needsStartup)
            {
                _statusCallback?.Invoke("Ollamaサーバーの接続を確認中...");
                _isAvailable = await CheckOllamaAvailabilityAsync();
            }

            if (!_isAvailable || needsStartup)
            {
                _statusCallback?.Invoke("Ollamaサーバーを起動中...");
                _logMessage("Ollamaサーバーを起動するのだ...");
                await StartOllamaServerAsync();

                // GPU（CUDA）初期化に時間がかかる場合があるため、
                // リトライ付きポーリングで接続を確認する（最大90秒）
                const int maxRetries = 6;
                const int retryDelayMs = 15_000; // 15秒間隔
                for (var retry = 0; retry < maxRetries; retry++)
                {
                    var waitSec = (retry == 0) ? 10 : retryDelayMs / 1000;
                    _statusCallback?.Invoke($"Ollamaサーバーの応答を待機中...（{retry + 1}/{maxRetries}）");
                    _logMessage($"Ollamaサーバー応答待機中なのだ（試行 {retry + 1}/{maxRetries}、{waitSec}秒待機）...");
                    await Task.Delay(retry == 0 ? 10_000 : retryDelayMs);
                    _isAvailable = await CheckOllamaAvailabilityAsync();
                    if (_isAvailable) break;
                    _logMessage($"Ollamaサーバーがまだ応答しないのだ（試行 {retry + 1}/{maxRetries}）");
                }
            }

            if (_isAvailable)
            {
                _logMessage("Ollamaが利用可能なのだ。");
                _statusCallback?.Invoke("モデルの確認中...");
                var hasModel = await CheckModelAvailabilityAsync(DefaultModelName);
                if (!hasModel)
                {
                    _logMessage($"モデル '{DefaultModelName}' が見つからないのだ。ダウンロードを開始するのだ...");
                    _statusCallback?.Invoke($"{DefaultModelName} モデルをダウンロード中...");
                    await DownloadModelAsync(DefaultModelName);
                }
                _logMessage("Ollama環境の初期化が完了したのだ。");
            }
            else
            {
                _logError("Ollamaの起動に失敗したのだ。画像説明機能は無効になるのだ。");
            }
        }
        catch (Exception ex)
        {
            _logError($"Ollama初期化中にエラーが発生したのだ: {ex.Message}");
            _isAvailable = false;
        }
    }

    /// <summary>
    /// Ollamaが利用可能かチェックする。
    /// 成功時はレスポンスをキャッシュし、直後の CheckModelAvailabilityAsync で再利用する。
    /// </summary>
    private async Task<bool> CheckOllamaAvailabilityAsync()
    {
        try
        {
            _logMessage("Ollamaの接続テストを実行中なのだ...");
            var response = await _httpClient.GetAsync($"{_ollamaUrl}/api/tags");

            if (response.IsSuccessStatusCode)
            {
                _logMessage("Ollamaへの接続に成功したのだ。");
                // 後続の CheckModelAvailabilityAsync で再利用するためキャッシュ
                _cachedTagsJson = await response.Content.ReadAsStringAsync();
                return true;
            }
            else
            {
                _logError($"Ollamaへの接続に失敗したのだ。ステータスコード: {response.StatusCode}");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logError($"Ollamaへの接続エラーなのだ: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logError($"Ollama接続テスト中にエラーなのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 指定されたモデルが利用可能かチェックする。
    /// CheckOllamaAvailabilityAsync でキャッシュ済みのレスポンスがあれば再利用し、
    /// /api/tags への二重リクエストを回避する。
    /// </summary>
    private async Task<bool> CheckModelAvailabilityAsync(string modelName)
    {
        try
        {
            _logMessage($"モデル '{modelName}' の可用性を確認中なのだ...");

            // キャッシュ済みレスポンスがあれば再利用（API呼び出しを1回削減）
            string content;
            if (_cachedTagsJson is not null)
            {
                content = _cachedTagsJson;
                _cachedTagsJson = null; // 1回限りのキャッシュ
            }
            else
            {
                var response = await _httpClient.GetAsync($"{_ollamaUrl}/api/tags");
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }
                content = await response.Content.ReadAsStringAsync();
            }

            using var jsonDoc = JsonDocument.Parse(content);

            if (jsonDoc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var nameStr = name.GetString();
                        if (nameStr != null && nameStr.StartsWith(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logMessage($"モデル '{modelName}' の確認が完了したのだ（インストール済み）。");
                            return true;
                        }
                    }
                }
            }

            _logMessage($"モデル '{modelName}' の確認が完了したのだ（未インストール）。");
            return false;
        }
        catch (Exception ex)
        {
            _logError($"モデル確認中にエラーなのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 利用可能なモデルのリストを取得する
    /// </summary>
    public async Task<string[]> GetAvailableModelsAsync()
    {
        try
        {
            if (!_isAvailable)
            {
                return Array.Empty<string>();
            }

            var response = await _httpClient.GetAsync($"{_ollamaUrl}/api/tags");
            
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            
            var modelList = new System.Collections.Generic.List<string>();
            
            if (jsonDoc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var nameStr = name.GetString();
                        if (nameStr != null)
                        {
                            modelList.Add(nameStr);
                        }
                    }
                }
            }

            return modelList.ToArray();
        }
        catch (Exception ex)
        {
            _logError($"モデル一覧取得中にエラーなのだ: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Ollamaをダウンロードして展開する
    /// </summary>
    private async Task<bool> DownloadAndExtractOllamaAsync(string ollamaBaseDir)
    {
        if (_isExtracting)
        {
            _logMessage("既に展開処理が実行中なのだ。");
            return false;
        }
        try
        {
            _logMessage("GitHub から Ollama をダウンロード中なのだ...");
            const string downloadUrl = "https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip";
            const string fileName = "ollama-windows-amd64.zip";

            Directory.CreateDirectory(ollamaBaseDir);
            var archivePath = Path.Combine(ollamaBaseDir, fileName);

            _logMessage($"Ollamaをダウンロード中なのだ: {downloadUrl}");
            _statusCallback?.Invoke("Ollama本体をダウンロード中...");
            _progressCallback?.Invoke(0);

            using (var response = await HttpClientForDownload.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                // ダウンロードサイズの上限チェック（2GB以上は拒否）
                const long MaxDownloadSize = 2L * 1024L * 1024L * 1024L; // 2GB

                if (totalBytes <= 0)
                {
                    _logWarning("警告: ダウンロードサイズが不明なのだ。続行するのだ。");
                }
                else if (totalBytes > MaxDownloadSize)
                {
                    _logError($"エラー: ダウンロードサイズが大きすぎるのだ（{totalBytes / 1024.0 / 1024.0 / 1024.0:F2}GB > 2GB）");
                    throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                }
                else
                {
                    _logMessage($"ダウンロードサイズなのだ: {totalBytes / 1024 / 1024:F2} MB");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920]; // 80KB — ネットワークI/Oのシステムコール回数を削減
                var totalBytesRead = 0L;
                int bytesRead;
                var lastReportedProgress = 0.0;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    // ダウンロードサイズの追加チェック
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxDownloadSize)
                    {
                        _logError($"エラー: ダウンロードサイズが上限を超えたのだ");
                        throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)totalBytesRead / totalBytes * 100;

                        if (progress - lastReportedProgress >= 0.5)
                        {
                            _progressCallback?.Invoke(progress);
                            lastReportedProgress = progress;
                        }

                        if (totalBytesRead / (1024 * 1024) > (totalBytesRead - bytesRead) / (1024 * 1024))
                        {
                            _logMessage($"ダウンロード進捗なのだ: {progress:F1}% ({totalBytesRead / 1024 / 1024:F2} MB / {totalBytes / 1024 / 1024:F2} MB)");
                        }
                    }
                }
            }

            _progressCallback?.Invoke(100);
            _logMessage("ダウンロード完了なのだ");
            await Task.Delay(500);

            _isExtracting = true;
            _progressCallback?.Invoke(0);
            _statusCallback?.Invoke("Ollamaを展開中...");
            _logMessage("Ollamaアーカイブを展開中なのだ...");
            
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var totalEntries = archive.Entries.Count;
                var extractedCount = 0;
                
                _logMessage($"展開するファイル数なのだ: {totalEntries}");
                
                foreach (var entry in archive.Entries)
                {
                    var destinationPath = Path.Combine(ollamaBaseDir, entry.FullName);
                    
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        entry.ExtractToFile(destinationPath, true);
                    }
                    
                    extractedCount++;
                    var progress = (double)extractedCount / totalEntries * 100;
                    
                    if (extractedCount % 5 == 0 || extractedCount == totalEntries)
                    {
                        _progressCallback?.Invoke(progress);
                        _statusCallback?.Invoke($"Ollamaを展開中... {progress:F0}%");
                        _logMessage($"展開中なのだ: {extractedCount}/{totalEntries} ファイル ({progress:F1}%)");
                    }
                }
            });
            
            _progressCallback?.Invoke(100);
            _logMessage("Ollamaの展開が完了したのだ。");

            await Task.Delay(500);

            _logMessage("アーカイブファイルを削除中なのだ...");
            try
            {
                File.Delete(archivePath);
                _logMessage("アーカイブファイルを削除したのだ。");
            }
            catch (IOException ex)
            {
                _logWarning($"アーカイブファイルの削除に失敗したのだ: {ex.Message}");
            }

            _logMessage("Ollama実行ファイルを確認中なのだ...");
            var ollamaExe = Path.Combine(ollamaBaseDir, "ollama.exe");
            if (File.Exists(ollamaExe))
            {
                _ollamaExePath = ollamaExe;
                _logMessage($"Ollama実行ファイルを確認したのだ: {_ollamaExePath}");
                return true;
            }

            _logMessage("Ollama実行ファイルが見つからないのだ。");
            return false;
        }
        catch (Exception ex)
        {
            _logError($"Ollamaのダウンロード/展開に失敗したのだ: {ex.Message}");
            return false;
        }
        finally
        {
            _isExtracting = false;
        }
    }

    /// <summary>
    /// Ollamaサーバーを起動する
    /// </summary>
    private async Task StartOllamaServerAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_ollamaExePath) || !File.Exists(_ollamaExePath))
            {
                _logMessage("Ollama実行ファイルが見つからないのだ。");
                return;
            }

            _logMessage("Ollamaサーバーを起動中なのだ...");

            var startInfo = new ProcessStartInfo
            {
                FileName = _ollamaExePath,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_ollamaExePath)
            };

            // URLプロトコルを削除（http://またはhttps://）
            var ollamaHost = _ollamaUrl
                .Replace("https://", "")
                .Replace("http://", "");
            startInfo.Environment["OLLAMA_HOST"] = ollamaHost;
            
            var gpuDevice = AppSettings.GetOllamaGpuDevice()?.Trim();
            if (!string.IsNullOrEmpty(gpuDevice) && gpuDevice != "0")
            {
                startInfo.Environment["CUDA_VISIBLE_DEVICES"] = gpuDevice;
                _logMessage($"GPU設定なのだ: CUDA_VISIBLE_DEVICES={gpuDevice}");
            }
            else
            {
                _logMessage("GPU設定: 自動検出なのだ（CUDA_VISIBLE_DEVICES未設定）");
            }

            _ollamaProcess = Process.Start(startInfo);
            if (_ollamaProcess != null)
            {
                _logMessage("Ollamaサーバーを起動したのだ。");
                
                _ollamaProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logMessage($"Ollama: {e.Data}");
                    }
                };
                _ollamaProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        var logLevel = GetOllamaLogLevel(e.Data);
                        _logMessage($"Ollama[{logLevel}]: {e.Data}");
                    }
                };
                
                _ollamaProcess.BeginOutputReadLine();
                _ollamaProcess.BeginErrorReadLine();
            }
            else
            {
                _logError("Ollamaプロセスの起動に失敗したのだ。");
            }
        }
        catch (Exception ex)
        {
            _logError($"Ollamaサーバー起動中にエラーなのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// Ollamaのログレベルを判定する
    /// </summary>
    /// <param name="logMessage">ログメッセージ</param>
    /// <returns>ログレベル（INFO, WARN, ERROR）</returns>
    private static string GetOllamaLogLevel(string logMessage)
    {
        if (logMessage.Contains("level=ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR";
        }
        if (logMessage.Contains("level=WARN", StringComparison.OrdinalIgnoreCase))
        {
            return "WARN";
        }
        if (logMessage.Contains("level=INFO", StringComparison.OrdinalIgnoreCase))
        {
            return "INFO";
        }
        return "INFO";
    }

    /// <summary>
    /// モデルをダウンロードする（リアルタイム進捗表示付き）
    /// </summary>
    private async Task DownloadModelAsync(string modelName)
    {
        try
        {
            if (string.IsNullOrEmpty(_ollamaExePath) || !File.Exists(_ollamaExePath))
            {
                _logMessage("Ollama実行ファイルが見つからないのだ。");
                return;
            }

            _logMessage($"モデル '{modelName}' をダウンロード中なのだ...");
            _progressCallback?.Invoke(0);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ollamaExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_ollamaExePath)
            };

            // ArgumentList を使用してコマンドインジェクションを防止
            startInfo.ArgumentList.Add("pull");
            startInfo.ArgumentList.Add(modelName);

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                var tcs = new TaskCompletionSource<bool>();

                // stderr からリアルタイムで進捗をパース
                // ollama pull の出力例:
                //   pulling manifest
                //   pulling abc123... 45% ▕██████        ▏ 1.5 GB/3.3 GB
                //   verifying sha256 digest
                //   writing manifest
                //   success
                var lastLoggedPercent = -1;

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data))
                        return;

                    var line = e.Data;

                    // 進捗パーセントをパース（例: "45%" や "pulling abc123... 45%"）
                    var percentMatch = PercentRegex.Match(line);
                    if (percentMatch.Success && int.TryParse(percentMatch.Groups[1].Value, out var percent))
                    {
                        _progressCallback?.Invoke(percent);

                        // ステータスにサイズ情報を含める（例: "1.5 GB/3.3 GB"）
                        var sizeMatch = SizeRegex.Match(line);
                        if (sizeMatch.Success)
                        {
                            _statusCallback?.Invoke(
                                $"{modelName} ダウンロード中... {percent}%（{sizeMatch.Groups[1].Value}/{sizeMatch.Groups[2].Value}）");
                        }
                        else
                        {
                            _statusCallback?.Invoke($"{modelName} ダウンロード中... {percent}%");
                        }

                        // ログは10%刻みで出力（大量のログを防ぐ）
                        if (percent / 10 > lastLoggedPercent / 10)
                        {
                            lastLoggedPercent = percent;
                            _logMessage($"モデルダウンロード進捗なのだ: {percent}% - {line.Trim()}");
                        }
                    }
                    else
                    {
                        // 進捗以外のステータス行（pulling manifest, verifying, writing manifest 等）
                        if (line.Contains("pulling manifest", StringComparison.OrdinalIgnoreCase))
                        {
                            _statusCallback?.Invoke($"{modelName} マニフェストを取得中...");
                        }
                        else if (line.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                        {
                            _statusCallback?.Invoke($"{modelName} を検証中...");
                            _progressCallback?.Invoke(100);
                        }
                        else if (line.Contains("writing manifest", StringComparison.OrdinalIgnoreCase))
                        {
                            _statusCallback?.Invoke($"{modelName} のマニフェストを書き込み中...");
                        }
                        else if (line.Contains("success", StringComparison.OrdinalIgnoreCase))
                        {
                            _statusCallback?.Invoke($"{modelName} のダウンロード完了");
                            _progressCallback?.Invoke(100);
                        }

                        _logMessage($"Ollama pull: {line.Trim()}");
                    }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logMessage($"Ollama pull出力: {e.Data}");
                    }
                };

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                await process.WaitForExitAsync(cts.Token);

                if (process.ExitCode == 0)
                {
                    _logMessage($"モデル '{modelName}' のダウンロードが完了したのだ。");
                    _statusCallback?.Invoke($"{modelName} モデルの準備完了");
                    _progressCallback?.Invoke(100);
                }
                else
                {
                    _logError($"モデル '{modelName}' のダウンロードに失敗したのだ。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logMessage($"モデル '{modelName}' のダウンロードがタイムアウトしたのだ。");
        }
        catch (Exception ex)
        {
            _logError($"モデルダウンロード中にエラーなのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// リソースを解放する
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// デストラクタ（ファイナライザ）
    /// </summary>
    ~OllamaManager()
    {
        Dispose(false);
    }

    /// <summary>
    /// リソースを解放する（内部実装）
    /// </summary>
    /// <param name="disposing">マネージドリソースも解放するかどうか</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // アプリ終了時にOllamaプロセスを確実に終了する
            if (_ollamaProcess != null)
            {
                try
                {
                    if (!_ollamaProcess.HasExited)
                    {
                        _ollamaProcess.Kill(true);
                        _ollamaProcess.WaitForExit(5000);
                    }
                }
                catch
                {
                    // プロセス終了時のエラーは無視
                }

                if (disposing)
                {
                    _ollamaProcess.Dispose();
                }
            }

            // 孤立したOllamaプロセスも終了する
            KillOrphanedOllamaProcesses();

            if (disposing)
            {
                _httpClient?.Dispose();
            }
        }
        catch
        {
            // 終了時のエラーは無視
        }
        finally
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// 孤立したOllamaプロセスを強制終了する
    /// </summary>
    private void KillOrphanedOllamaProcesses()
    {
        try
        {
            if (string.IsNullOrEmpty(_ollamaExePath))
            {
                return;
            }

            var ollamaProcesses = Process.GetProcessesByName("ollama");
            foreach (var process in ollamaProcesses)
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    // プロセスパスが取得できた場合のみ比較
                    if (!string.IsNullOrEmpty(processPath) &&
                        processPath.Equals(_ollamaExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            _logMessage($"孤立したOllamaプロセスを終了するのだ (PID: {process.Id})");
                        }
                        catch
                        {
                            // ログ出力が失敗しても続行
                        }
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // プロセスが既に終了している場合
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // アクセス権限がない場合
                }
                catch (Exception ex)
                {
                    // 予期しないエラーはログに記録
                    try
                    {
                        _logWarning($"孤立プロセス終了中のエラーなのだ (PID: {process.Id}): {ex.Message}");
                    }
                    catch
                    {
                        // ログ出力が失敗しても続行
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // 孤立プロセスのクリーンアップ失敗時も具体的なエラーを記録
            try
            {
                _logWarning($"孤立Ollamaプロセス取得エラーなのだ: {ex.Message}");
            }
            catch
            {
                // ログ出力が失敗しても無視
            }
        }
    }
}
