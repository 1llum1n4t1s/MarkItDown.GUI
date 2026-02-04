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
    public FfmpegManager(Action<string> logMessage)
    {
        _logMessage = logMessage;
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
            _logMessage("ffmpeg 環境の初期化を開始します。");
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var ffmpegBaseDir = Path.Combine(appDirectory, "lib", "ffmpeg");
            _logMessage($"ffmpeg ディレクトリ: {ffmpegBaseDir}");

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
                        _logMessage($"既存の ffmpeg を検出しました: {_ffmpegPath}");
                        return;
                    }
                }
            }

            _logMessage("埋め込み ffmpeg が見つからないため、ダウンロードを試行します。");
            if (await DownloadAndExtractFfmpegAsync(ffmpegBaseDir))
            {
                _ffmpegAvailable = true;
                _logMessage($"ffmpeg の準備が完了しました: {_ffmpegPath}");
            }
            else
            {
                _logMessage("ffmpeg の準備に失敗しました。音声ファイルの変換に制限が発生する可能性があります。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"ffmpeg 初期化中に例外: {ex.Message}");
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
            _logMessage("gyan.dev から ffmpeg release essentials をダウンロード中...");
            const string downloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";
            const string fileName = "ffmpeg-release-essentials.7z";

            Directory.CreateDirectory(ffmpegBaseDir);
            var archivePath = Path.Combine(ffmpegBaseDir, fileName);

            _logMessage($"ffmpeg をダウンロード中: {downloadUrl}");
            using (var response = await HttpClientForDownload.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
                }
            }

            _logMessage("ダウンロード完了、ファイルハンドルを解放中...");
            await Task.Delay(200);

            _logMessage("ffmpeg を展開中...");
            await ExtractSevenZipAsync(archivePath, ffmpegBaseDir);

            _logMessage("展開完了後の待機中...");
            await Task.Delay(500);

            try
            {
                File.Delete(archivePath);
                _logMessage("アーカイブファイルを削除しました。");
            }
            catch (IOException ex)
            {
                _logMessage($"アーカイブファイルの削除に失敗しました（処理は継続）: {ex.Message}");
            }

            var extractedDirs = Directory.GetDirectories(ffmpegBaseDir, "ffmpeg-*");
            if (extractedDirs.Length > 0)
            {
                _logMessage($"展開されたディレクトリ: {string.Join(", ", extractedDirs.Select(Path.GetFileName))}");
                var binDir = Path.Combine(extractedDirs[0], "bin");
                if (Directory.Exists(binDir) && File.Exists(Path.Combine(binDir, "ffmpeg.exe")))
                {
                    _ffmpegPath = binDir;
                    _logMessage($"ffmpeg の展開が完了しました: {_ffmpegPath}");
                    return true;
                }
                _logMessage($"binディレクトリが見つかりません: {binDir}");
            }
            else
            {
                _logMessage($"ffmpeg-* パターンのディレクトリが見つかりません。");
                var allDirs = Directory.GetDirectories(ffmpegBaseDir);
                if (allDirs.Length > 0)
                {
                    _logMessage($"存在するディレクトリ: {string.Join(", ", allDirs.Select(Path.GetFileName))}");
                }
            }

            _logMessage("ffmpeg の展開に失敗しました。");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"ffmpeg のダウンロード/展開に失敗しました: {ex.Message}");
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
                    _logMessage($"7zアーカイブを展開中: {archivePath} (試行 {retryCount + 1}/{maxRetries})");
                    
                    using (var reader = new ArchiveReader(archivePath))
                    {
                        _logMessage($"展開先: {destinationDir}");
                        reader.Save(destinationDir);
                    }
                    
                    _logMessage("7zアーカイブの展開が完了しました。");
                });
                
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                return;
            }
            catch (IOException ex) when (retryCount < maxRetries - 1)
            {
                retryCount++;
                _logMessage($"ファイルアクセスエラー（リトライ {retryCount}/{maxRetries}）: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }
}
