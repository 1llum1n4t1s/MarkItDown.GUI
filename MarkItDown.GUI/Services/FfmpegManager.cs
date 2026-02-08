using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cube.FileSystem.SevenZip;

namespace MarkItDown.GUI.Services;

/// <summary>
/// アプリ内完結の埋め込み ffmpeg を管理する
/// </summary>
public partial class FfmpegManager
{
    private readonly Action<string> _logMessage;
    private readonly Action<double>? _progressCallback;
    private readonly SemaphoreSlim _ffmpegDetectionSemaphore = new(1, 1);
    private string _ffmpegPath = string.Empty;
    private bool _ffmpegAvailable;

    private static readonly HttpClient HttpClientForDownload = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };


    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="logMessage">ログ出力用デリゲート</param>
    /// <param name="progressCallback">進捗コールバック関数（オプション）</param>
    public FfmpegManager(Action<string> logMessage, Action<double>? progressCallback = null)
    {
        _logMessage = logMessage;
        _progressCallback = progressCallback;
    }

    /// <summary>
    /// ffmpeg が利用可能かどうか
    /// </summary>
    public bool IsFfmpegAvailable => _ffmpegAvailable;

    /// <summary>
    /// ffmpeg の bin フォルダパス（PATH 追加用）
    /// </summary>
    public string FfmpegBinPath => _ffmpegPath;

    /// <summary>
    /// 埋め込み ffmpeg を非同期で初期化する
    /// </summary>
    public async Task InitializeAsync()
    {
        await _ffmpegDetectionSemaphore.WaitAsync();
        try
        {
            _logMessage("ffmpeg 環境の初期化を開始するのだ。");
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var ffmpegBaseDir = Path.Combine(appDirectory, "lib", "ffmpeg");
            _logMessage($"ffmpeg ディレクトリなのだ: {ffmpegBaseDir}");

            if (Directory.Exists(ffmpegBaseDir))
            {
                var ffmpegDirs = Directory.GetDirectories(ffmpegBaseDir, "ffmpeg-*");
                foreach (var dir in ffmpegDirs)
                {
                    var binDir = Path.Combine(dir, "bin");
                    var ffmpegExe = Path.Combine(binDir, "ffmpeg.exe");
                    if (File.Exists(ffmpegExe))
                    {
                        _ffmpegPath = binDir;
                        _ffmpegAvailable = true;
                        _logMessage($"既存の ffmpeg を検出したのだ: {_ffmpegPath}");
                        return;
                    }
                }
            }

            _logMessage("埋め込み ffmpeg が見つからないため、ダウンロードを試行するのだ。");
            if (await DownloadAndExtractFfmpegAsync(ffmpegBaseDir))
            {
                _ffmpegAvailable = true;
                _logMessage($"ffmpeg の準備が完了したのだ: {_ffmpegPath}");
            }
            else
            {
                _logMessage("ffmpeg の準備に失敗したのだ。音声ファイルの変換に制限が発生する可能性があるのだ。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"ffmpeg 初期化中に例外なのだ: {ex.Message}");
        }
        finally
        {
            _ffmpegDetectionSemaphore.Release();
        }
    }

    /// <summary>
    /// gyan.dev から ffmpeg essentials ビルドをダウンロードして展開する
    /// </summary>
    private async Task<bool> DownloadAndExtractFfmpegAsync(string ffmpegBaseDir)
    {
        try
        {
            _logMessage("gyan.dev から ffmpeg release essentials をダウンロード中なのだ...");
            const string downloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";
            const string fileName = "ffmpeg-release-essentials.7z";

            Directory.CreateDirectory(ffmpegBaseDir);
            var archivePath = Path.Combine(ffmpegBaseDir, fileName);

            _logMessage($"ffmpeg をダウンロード中なのだ: {downloadUrl}");
            _progressCallback?.Invoke(0);
            
            using (var response = await HttpClientForDownload.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                // ダウンロードサイズの上限チェック（1GB以上は拒否）
                const long MaxDownloadSize = 1024L * 1024L * 1024L; // 1GB

                if (totalBytes <= 0)
                {
                    _logMessage("警告: ダウンロードサイズが不明なのだ。続行するのだ。");
                }
                else if (totalBytes > MaxDownloadSize)
                {
                    _logMessage($"エラー: ダウンロードサイズが大きすぎるのだ（{totalBytes / 1024 / 1024 / 1024}GB > 1GB）");
                    throw new InvalidOperationException($"ダウンロードサイズが上限を超えています");
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

            _logMessage("ダウンロード完了なのだ、ファイルハンドルを解放中なのだ...");
            await Task.Delay(200);

            _logMessage("ffmpeg を展開中なのだ...");
            _progressCallback?.Invoke(0);
            await Task.Delay(500);
            await ExtractSevenZipAsync(archivePath, ffmpegBaseDir);
            _progressCallback?.Invoke(100);

            _logMessage("展開完了後の待機中なのだ...");
            await Task.Delay(500);

            try
            {
                File.Delete(archivePath);
                _logMessage("アーカイブファイルを削除したのだ。");
            }
            catch (IOException ex)
            {
                _logMessage($"アーカイブファイルの削除に失敗したのだ（処理は継続するのだ）: {ex.Message}");
            }

            var extractedDirs = Directory.GetDirectories(ffmpegBaseDir, "ffmpeg-*");
            if (extractedDirs.Length > 0)
            {
                _logMessage($"展開されたディレクトリなのだ: {string.Join(", ", extractedDirs.Select(Path.GetFileName))}");
                var binDir = Path.Combine(extractedDirs[0], "bin");
                if (Directory.Exists(binDir) && File.Exists(Path.Combine(binDir, "ffmpeg.exe")))
                {
                    _ffmpegPath = binDir;
                    _logMessage($"ffmpeg の展開が完了したのだ: {_ffmpegPath}");
                    return true;
                }
                _logMessage($"binディレクトリが見つからないのだ: {binDir}");
            }
            else
            {
                _logMessage($"ffmpeg-* パターンのディレクトリが見つからないのだ。");
                var allDirs = Directory.GetDirectories(ffmpegBaseDir);
                if (allDirs.Length > 0)
                {
                    _logMessage($"存在するディレクトリなのだ: {string.Join(", ", allDirs.Select(Path.GetFileName))}");
                }
            }

            _logMessage("ffmpeg の展開に失敗したのだ。");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"ffmpeg のダウンロード/展開に失敗したのだ: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 7z アーカイブを展開する（Cube.FileSystem.SevenZipを使用）
    /// </summary>
    private async Task ExtractSevenZipAsync(string archivePath, string destinationDir)
    {
        const int maxRetries = 3;
        var retryCount = 0;
        
        while (retryCount < maxRetries)
        {
            try
            {
                await Task.Run(() =>
                {
                    _logMessage($"7zアーカイブを展開中なのだ: {archivePath} (試行 {retryCount + 1}/{maxRetries})");
                    
                    using (var reader = new ArchiveReader(archivePath))
                    {
                        _logMessage($"展開先なのだ: {destinationDir}");
                        reader.Save(destinationDir);
                    }
                    
                    _logMessage("7zアーカイブの展開が完了したのだ。");
                });
                
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                return;
            }
            catch (IOException ex) when (retryCount < maxRetries - 1)
            {
                retryCount++;
                _logMessage($"ファイルアクセスエラーなのだ（リトライ {retryCount}/{maxRetries}）: {ex.Message}");
                await Task.Delay(1000);
            }
        }

        // リトライ回数超過時の処理
        throw new IOException($"7zアーカイブの展開に失敗しました（最大リトライ回数 {maxRetries} 回を超過）");
    }
}
