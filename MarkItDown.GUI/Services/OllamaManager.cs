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
/// Ollamaとの連携を管理するクラス
/// </summary>
public class OllamaManager : IDisposable
{
    private readonly Action<string> _logMessage;
    private readonly Action<double>? _progressCallback;
    private readonly HttpClient _httpClient;
    private string _ollamaUrl = "http://localhost:11434";
    private const string DefaultModelName = "gemma3:4b";
    private bool _isAvailable;
    private Process? _ollamaProcess;
    private string _ollamaExePath = string.Empty;
    private bool _isExtracting;
    private bool _disposed;

    private static readonly HttpClient HttpClientForDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

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
    public OllamaManager(Action<string> logMessage, Action<double>? progressCallback = null)
    {
        _logMessage = logMessage;
        _progressCallback = progressCallback;
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

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var ollamaBaseDir = Path.Combine(appDirectory, "lib", "ollama");
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
                if (await DownloadAndExtractOllamaAsync(ollamaBaseDir))
                {
                    _logMessage($"Ollamaの準備が完了したのだ: {_ollamaExePath}");
                    needsStartup = true;
                }
                else
                {
                    _logMessage("Ollamaのダウンロードに失敗したのだ。画像説明機能は無効になるのだ。");
                    _isAvailable = false;
                    return;
                }
            }

            if (!needsStartup)
            {
                _isAvailable = await CheckOllamaAvailabilityAsync();
            }

            if (!_isAvailable || needsStartup)
            {
                _logMessage("Ollamaサーバーを起動するのだ...");
                await StartOllamaServerAsync();
                await Task.Delay(5000);
                _isAvailable = await CheckOllamaAvailabilityAsync();
            }

            if (_isAvailable)
            {
                _logMessage("Ollamaが利用可能なのだ。");
                var hasModel = await CheckModelAvailabilityAsync(DefaultModelName);
                if (!hasModel)
                {
                    _logMessage($"モデル '{DefaultModelName}' が見つからないのだ。ダウンロードを開始するのだ...");
                    await DownloadModelAsync(DefaultModelName);
                }
            }
            else
            {
                _logMessage("Ollamaの起動に失敗したのだ。画像説明機能は無効になるのだ。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Ollama初期化中にエラーが発生したのだ: {ex.Message}");
            _isAvailable = false;
        }
    }

    /// <summary>
    /// Ollamaが利用可能かチェックする
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
                return true;
            }
            else
            {
                _logMessage($"Ollamaへの接続に失敗したのだ。ステータスコード: {response.StatusCode}");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logMessage($"Ollamaへの接続エラーなのだ: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"Ollama接続テスト中にエラーなのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 指定されたモデルが利用可能かチェックする
    /// </summary>
    private async Task<bool> CheckModelAvailabilityAsync(string modelName)
    {
        try
        {
            _logMessage($"モデル '{modelName}' の可用性を確認中なのだ...");
            var response = await _httpClient.GetAsync($"{_ollamaUrl}/api/tags");
            
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            
            if (jsonDoc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var nameStr = name.GetString();
                        if (nameStr != null && nameStr.StartsWith(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logMessage($"モデル '{modelName}' が見つかったのだ。");
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"モデル確認中にエラーなのだ: {ex.Message}");
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
            _logMessage($"モデル一覧取得中にエラーなのだ: {ex.Message}");
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
            _progressCallback?.Invoke(0);

            using (var response = await HttpClientForDownload.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                // ダウンロードサイズの上限チェック（2GB以上は拒否）
                const long MaxDownloadSize = 2L * 1024L * 1024L * 1024L; // 2GB

                if (totalBytes <= 0)
                {
                    _logMessage("警告: ダウンロードサイズが不明なのだ。続行するのだ。");
                }
                else if (totalBytes > MaxDownloadSize)
                {
                    _logMessage($"エラー: ダウンロードサイズが大きすぎるのだ（{totalBytes / 1024.0 / 1024.0 / 1024.0:F2}GB > 2GB）");
                    throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                }
                else
                {
                    _logMessage($"ダウンロードサイズなのだ: {totalBytes / 1024 / 1024:F2} MB");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                var totalBytesRead = 0L;
                int bytesRead;
                var lastReportedProgress = 0.0;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // ダウンロードサイズの追加チェック
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxDownloadSize)
                    {
                        _logMessage($"エラー: ダウンロードサイズが上限を超えたのだ");
                        throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)totalBytesRead / totalBytes * 100;
                        
                        if (progress - lastReportedProgress >= 0.5 || bytesRead < buffer.Length)
                        {
                            _progressCallback?.Invoke(progress);
                            lastReportedProgress = progress;
                        }
                        
                        if (totalBytesRead % (1024 * 1024) == 0 || bytesRead < buffer.Length)
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
                        _logMessage($"展開中なのだ: {extractedCount}/{totalEntries} ファイル ({progress:F1}%)");
                    }
                }
            });
            
            _progressCallback?.Invoke(100);
            _isExtracting = false;
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
                _logMessage($"アーカイブファイルの削除に失敗したのだ: {ex.Message}");
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
            _logMessage($"Ollamaのダウンロード/展開に失敗したのだ: {ex.Message}");
            return false;
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
                _logMessage("Ollamaプロセスの起動に失敗したのだ。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Ollamaサーバー起動中にエラーなのだ: {ex.Message}");
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
    /// モデルをダウンロードする
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
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

                // ReadToEndAsync と WaitForExitAsync を並列実行してデッドロックを回避
                var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);

                var output = await outputTask;
                var error = await errorTask;

                if (!string.IsNullOrEmpty(output))
                {
                    _logMessage($"Ollama pull出力: {output}");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    _logMessage($"Ollama pullエラー: {error}");
                }

                if (process.ExitCode == 0)
                {
                    _logMessage($"モデル '{modelName}' のダウンロードが完了したのだ。");
                }
                else
                {
                    _logMessage($"モデル '{modelName}' のダウンロードに失敗したのだ。");
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"モデルダウンロード中にエラーなのだ: {ex.Message}");
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
            if (_ollamaProcess != null && !_ollamaProcess.HasExited)
            {
                try
                {
                    _logMessage("Ollamaサーバーを停止中なのだ...");
                }
                catch
                {
                    // ログ出力が失敗しても続行
                }
                
                try
                {
                    _ollamaProcess.Kill(true);
                    if (!_ollamaProcess.WaitForExit(5000))
                    {
                        try
                        {
                            _logMessage("Ollamaプロセスの終了を待機中にタイムアウトしたのだ。");
                        }
                        catch
                        {
                            // ログ出力が失敗しても続行
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // プロセスが既に終了している場合
                }
                catch
                {
                    // その他のエラーも無視して続行
                }
                finally
                {
                    if (disposing)
                    {
                        _ollamaProcess.Dispose();
                    }
                }
            }
            
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
                        _logMessage($"孤立プロセス終了中のエラーなのだ (PID: {process.Id}): {ex.Message}");
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
                _logMessage($"孤立Ollamaプロセス取得エラーなのだ: {ex.Message}");
            }
            catch
            {
                // ログ出力が失敗しても無視
            }
        }
    }
}
