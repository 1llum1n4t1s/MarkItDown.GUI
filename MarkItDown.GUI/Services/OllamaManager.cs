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
public class OllamaManager
{
    private readonly Action<string> _logMessage;
    private readonly Action<double>? _progressCallback;
    private readonly HttpClient _httpClient;
    private string _ollamaUrl = "http://localhost:11434";
    private string _defaultModel = "llava";
    private bool _isAvailable;
    private Process? _ollamaProcess;
    private string _ollamaExePath = string.Empty;

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
    public string DefaultModel => _defaultModel;

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
            _logMessage("Ollama環境の初期化を開始します...");

            var savedUrl = AppSettings.GetOllamaUrl();
            if (!string.IsNullOrEmpty(savedUrl))
            {
                _ollamaUrl = savedUrl;
            }

            var savedModel = AppSettings.GetOllamaModel();
            if (!string.IsNullOrEmpty(savedModel))
            {
                _defaultModel = savedModel;
            }

            _logMessage($"Ollama URL: {_ollamaUrl}");
            _logMessage($"Ollama モデル: {_defaultModel}");

            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var ollamaBaseDir = Path.Combine(appDirectory, "lib", "ollama");
            _logMessage($"Ollamaディレクトリ: {ollamaBaseDir}");

            if (Directory.Exists(ollamaBaseDir))
            {
                var ollamaExe = Path.Combine(ollamaBaseDir, "ollama.exe");
                if (File.Exists(ollamaExe))
                {
                    _ollamaExePath = ollamaExe;
                    _logMessage($"既存のOllamaを検出しました: {_ollamaExePath}");
                }
            }

            if (string.IsNullOrEmpty(_ollamaExePath))
            {
                _logMessage("埋め込みOllamaが見つからないため、ダウンロードを試行します。");
                if (await DownloadAndExtractOllamaAsync(ollamaBaseDir))
                {
                    _logMessage($"Ollamaの準備が完了しました: {_ollamaExePath}");
                }
                else
                {
                    _logMessage("Ollamaのダウンロードに失敗しました。画像説明機能は無効になります。");
                    _isAvailable = false;
                    return;
                }
            }

            _isAvailable = await CheckOllamaAvailabilityAsync();

            if (!_isAvailable)
            {
                _logMessage("Ollamaサーバーが起動していないため、起動を試みます...");
                await StartOllamaServerAsync();
                await Task.Delay(3000);
                _isAvailable = await CheckOllamaAvailabilityAsync();
            }

            if (_isAvailable)
            {
                _logMessage("Ollamaが利用可能です。");
                var hasModel = await CheckModelAvailabilityAsync(_defaultModel);
                if (!hasModel)
                {
                    _logMessage($"モデル '{_defaultModel}' が見つかりません。ダウンロードを開始します...");
                    await DownloadModelAsync(_defaultModel);
                }
            }
            else
            {
                _logMessage("Ollamaの起動に失敗しました。画像説明機能は無効になります。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Ollama初期化中にエラーが発生しました: {ex.Message}");
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
            _logMessage("Ollamaの接続テストを実行中...");
            var response = await _httpClient.GetAsync($"{_ollamaUrl}/api/tags");
            
            if (response.IsSuccessStatusCode)
            {
                _logMessage("Ollamaへの接続に成功しました。");
                return true;
            }
            else
            {
                _logMessage($"Ollamaへの接続に失敗しました。ステータスコード: {response.StatusCode}");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logMessage($"Ollamaへの接続エラー: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"Ollama接続テスト中にエラー: {ex.Message}");
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
            _logMessage($"モデル '{modelName}' の可用性を確認中...");
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
                            _logMessage($"モデル '{modelName}' が見つかりました。");
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"モデル確認中にエラー: {ex.Message}");
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
            _logMessage($"モデル一覧取得中にエラー: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Ollamaをダウンロードして展開する
    /// </summary>
    private async Task<bool> DownloadAndExtractOllamaAsync(string ollamaBaseDir)
    {
        try
        {
            _logMessage("GitHub から Ollama をダウンロード中...");
            const string downloadUrl = "https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip";
            const string fileName = "ollama-windows-amd64.zip";

            Directory.CreateDirectory(ollamaBaseDir);
            var archivePath = Path.Combine(ollamaBaseDir, fileName);

            _logMessage($"Ollamaをダウンロード中: {downloadUrl}");
            _progressCallback?.Invoke(0);

            using (var response = await HttpClientForDownload.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                
                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                _logMessage($"ダウンロードサイズ: {totalBytes / 1024 / 1024:F2} MB");
                
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
                
                var buffer = new byte[8192];
                var totalBytesRead = 0L;
                int bytesRead;
                
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)totalBytesRead / totalBytes * 100;
                        _progressCallback?.Invoke(progress);
                        
                        if (totalBytesRead % (1024 * 1024) == 0 || bytesRead < buffer.Length)
                        {
                            _logMessage($"ダウンロード進捗: {progress:F1}% ({totalBytesRead / 1024 / 1024:F2} MB / {totalBytes / 1024 / 1024:F2} MB)");
                        }
                    }
                }
            }

            _progressCallback?.Invoke(100);
            _logMessage("ダウンロード完了、展開中...");
            await Task.Delay(1000);

            _progressCallback?.Invoke(0);
            
            ZipFile.ExtractToDirectory(archivePath, ollamaBaseDir, true);
            _logMessage("Ollamaの展開が完了しました。");

            await Task.Delay(500);

            try
            {
                File.Delete(archivePath);
                _logMessage("アーカイブファイルを削除しました。");
            }
            catch (IOException ex)
            {
                _logMessage($"アーカイブファイルの削除に失敗しました: {ex.Message}");
            }

            var ollamaExe = Path.Combine(ollamaBaseDir, "ollama.exe");
            if (File.Exists(ollamaExe))
            {
                _ollamaExePath = ollamaExe;
                _logMessage($"Ollama実行ファイルを確認しました: {_ollamaExePath}");
                return true;
            }

            _logMessage("Ollama実行ファイルが見つかりません。");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"Ollamaのダウンロード/展開に失敗しました: {ex.Message}");
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
                _logMessage("Ollama実行ファイルが見つかりません。");
                return;
            }

            _logMessage("Ollamaサーバーを起動中...");

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

            startInfo.Environment["OLLAMA_HOST"] = _ollamaUrl.Replace("http://", "");

            _ollamaProcess = Process.Start(startInfo);
            if (_ollamaProcess != null)
            {
                _logMessage("Ollamaサーバーを起動しました。");
                
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
                        _logMessage($"Ollamaエラー: {e.Data}");
                    }
                };
                
                _ollamaProcess.BeginOutputReadLine();
                _ollamaProcess.BeginErrorReadLine();
            }
            else
            {
                _logMessage("Ollamaプロセスの起動に失敗しました。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Ollamaサーバー起動中にエラー: {ex.Message}");
        }
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
                _logMessage("Ollama実行ファイルが見つかりません。");
                return;
            }

            _logMessage($"モデル '{modelName}' をダウンロード中...");

            var startInfo = new ProcessStartInfo
            {
                FileName = _ollamaExePath,
                Arguments = $"pull {modelName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_ollamaExePath)
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

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
                    _logMessage($"モデル '{modelName}' のダウンロードが完了しました。");
                }
                else
                {
                    _logMessage($"モデル '{modelName}' のダウンロードに失敗しました。");
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"モデルダウンロード中にエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// リソースを解放する
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (_ollamaProcess != null && !_ollamaProcess.HasExited)
            {
                _logMessage("Ollamaサーバーを停止中...");
                _ollamaProcess.Kill();
                _ollamaProcess.WaitForExit(5000);
                _ollamaProcess.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Ollamaプロセス停止中にエラー: {ex.Message}");
        }
    }
}
