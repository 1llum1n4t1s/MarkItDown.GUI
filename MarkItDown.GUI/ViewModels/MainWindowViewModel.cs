using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using MarkItDown.GUI.Services;

namespace MarkItDown.GUI.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel
/// </summary>
public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private string _logText = "アプリケーションが起動したのだ...";
    private IBrush _dropZoneBackground;
    private FileProcessor? _fileProcessor;
    private OllamaManager? _ollamaManager;
    private WebScraperService? _webScraperService;
    private bool _isProcessing;
    private string _processingTitle = string.Empty;
    private string _processingStatus = string.Empty;
    private double _downloadProgress;
    private bool _isDownloading;
    private string _urlInput = string.Empty;
    private readonly List<string> _logBatch = new();
    private readonly object _logBatchLock = new();
    private const int MaxLogLines = 10000; // ログ行数上限（メモリ節約）
    private int _logLineCount = 1;

    private static readonly IBrush DefaultDropZoneBrush = new SolidColorBrush(Color.Parse("#D3D3D3"));
    private static readonly IBrush DragOverBrush = new SolidColorBrush(Color.Parse("#ADD8E6"));
    private static readonly IBrush ProcessingBrush = new SolidColorBrush(Color.Parse("#FFFFE0"));

    /// <summary>
    /// ログ表示テキスト
    /// </summary>
    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    /// <summary>
    /// ドロップゾーンの背景ブラシ
    /// </summary>
    public IBrush DropZoneBackground
    {
        get => _dropZoneBackground;
        set => SetProperty(ref _dropZoneBackground, value);
    }

    /// <summary>
    /// 処理中かどうか
    /// </summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    /// <summary>
    /// 処理タイトル
    /// </summary>
    public string ProcessingTitle
    {
        get => _processingTitle;
        set => SetProperty(ref _processingTitle, value);
    }

    /// <summary>
    /// 処理ステータスメッセージ
    /// </summary>
    public string ProcessingStatus
    {
        get => _processingStatus;
        set => SetProperty(ref _processingStatus, value);
    }

    /// <summary>
    /// ダウンロード進捗率（0～100）
    /// </summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => SetProperty(ref _downloadProgress, value);
    }

    /// <summary>
    /// ダウンロード中かどうか
    /// </summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    /// <summary>
    /// URL入力テキスト
    /// </summary>
    public string UrlInput
    {
        get => _urlInput;
        set => SetProperty(ref _urlInput, value);
    }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public MainWindowViewModel()
    {
        _dropZoneBackground = DefaultDropZoneBrush;
        _ = InitializeManagersAsync().ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                var ex = task.Exception?.GetBaseException();
                LogMessage($"初期化エラーなのだ: {ex?.Message}", LogLevel.Error);
            }
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// マネージャークラスを非同期で初期化する（埋め込みPythonと ffmpeg の検出またはダウンロードを含む）
    /// </summary>
    private async Task InitializeManagersAsync()
    {
        try
        {
            ShowProcessing("MarkItDown.GUI を初期化中...", "Python環境を初期化中...");

            var pythonEnvironmentManager = new PythonEnvironmentManager(LogMessage, UpdatePythonDownloadProgress, logError: LogError, logWarning: LogWarning);
            await pythonEnvironmentManager.InitializeAsync();

            if (!pythonEnvironmentManager.IsPythonAvailable)
            {
                LogMessage("Python環境の初期化に失敗したのだ。", LogLevel.Error);
                return;
            }

            UpdateProcessingStatus("ffmpeg環境を準備中...");
            var ffmpegManager = new FfmpegManager(LogMessage, UpdateFfmpegDownloadProgress, logError: LogError, logWarning: LogWarning);
            await ffmpegManager.InitializeAsync();

            UpdateProcessingStatus("Ollama環境を確認中...");
            _ollamaManager = new OllamaManager(LogMessage, UpdateOllamaDownloadProgress, UpdateOllamaStatus, logError: LogError, logWarning: LogWarning);
            await _ollamaManager.InitializeAsync();

            var pythonExe = pythonEnvironmentManager.PythonExecutablePath;
            var ffmpegBinPath = ffmpegManager.IsFfmpegAvailable ? ffmpegManager.FfmpegBinPath : null;
            var ollamaUrl = _ollamaManager.IsOllamaAvailable ? _ollamaManager.OllamaUrl : null;
            var ollamaModel = _ollamaManager.IsOllamaAvailable ? _ollamaManager.DefaultModel : null;

            UpdateProcessingStatus("Pythonパッケージを確認中...");
            var pythonPackageManager = new PythonPackageManager(pythonExe, LogMessage, logError: LogError, logWarning: LogWarning);
            await pythonPackageManager.InstallMarkItDownPackageAsync();

            UpdateProcessingStatus("MarkItDownライブラリを準備中...");
            var markItDownProcessor = new MarkItDownProcessor(pythonExe, LogMessage, ffmpegBinPath, ollamaUrl, ollamaModel, logError: LogError);
            _fileProcessor = new FileProcessor(markItDownProcessor, LogMessage, logError: LogError);

            _webScraperService = new WebScraperService(LogMessage, UpdateProcessingStatus, logError: LogError);
            var playwrightScraper = new PlaywrightScraperService(pythonExe, LogMessage, UpdateProcessingStatus, logError: LogError);

            // Ollama が利用可能な場合、Playwright/WebScraper に設定を渡す
            if (_ollamaManager is { IsOllamaAvailable: true })
            {
                playwrightScraper.SetOllamaConfig(_ollamaManager.OllamaUrl, _ollamaManager.DefaultModel);
                _webScraperService.SetOllamaConfig(_ollamaManager.OllamaUrl, _ollamaManager.DefaultModel);
            }

            _webScraperService.SetPlaywrightScraper(playwrightScraper);

            UpdateProcessingStatus("初期化完了");
            LogMessage("すべての初期化が完了したのだ！");
        }
        catch (Exception ex)
        {
            LogMessage($"初期化中にエラーが発生したのだ: {ex.Message}", LogLevel.Error);
            Logger.LogException("初期化中にエラーが発生しました", ex);
        }
        finally
        {
            await Task.Delay(500);
            HideProcessing();
        }
    }

    /// <summary>
    /// ログメッセージを画面に追加する（バッチ処理版）
    /// </summary>
    /// <param name="message">表示するメッセージ</param>
    public void LogMessage(string message) => LogMessage(message, LogLevel.Info);

    /// <summary>
    /// エラーレベルでログメッセージを画面に追加する
    /// </summary>
    public void LogError(string message) => LogMessage(message, LogLevel.Error);

    /// <summary>
    /// 警告レベルでログメッセージを画面に追加する
    /// </summary>
    public void LogWarning(string message) => LogMessage(message, LogLevel.Warning);

    /// <summary>
    /// ログメッセージを指定レベルで画面に追加する
    /// </summary>
    public void LogMessage(string message, LogLevel level)
    {
        try
        {
            Logger.Log(message, level);

            lock (_logBatchLock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _logBatch.Add($"[{timestamp}] {message}");

                // バッチサイズが 10 に達したら UI スレッドにポスト
                if (_logBatch.Count >= 10)
                {
                    FlushLogBatch();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to log message: {ex.Message}");
            Logger.LogException("ログメッセージの追加に失敗しました", ex);
        }
    }

    /// <summary>
    /// ログバッチを UI スレッドにフラッシュ（StringBuilder版で大規模ログを効率化）
    /// </summary>
    private void FlushLogBatch()
    {
        if (_logBatch.Count == 0)
        {
            return;
        }

        // StringBuilderで効率的に結合
        var sb = new System.Text.StringBuilder(_logBatch.Count * 50);
        foreach (var msg in _logBatch)
        {
            sb.AppendLine(msg);
            _logLineCount++;
        }
        var batchContent = sb.ToString();
        _logBatch.Clear();

        Dispatcher.UIThread.Post(() =>
        {
            // ログ行数上限超過時は古いログを削除
            if (_logLineCount > MaxLogLines)
            {
                var lines = LogText.Split('\n');
                int linesToRemove = Math.Max(1, _logLineCount - MaxLogLines);
                LogText = string.Join("\n", lines.Skip(linesToRemove));
                _logLineCount = MaxLogLines;
            }
            LogText += batchContent;
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 残っているログバッチを全てフラッシュする
    /// </summary>
    private void FlushLogBatchFinal()
    {
        lock (_logBatchLock)
        {
            FlushLogBatch();
        }
    }

    /// <summary>
    /// ドラッグオーバー時にドロップゾーンの背景を更新する
    /// </summary>
    public void SetDropZoneDragOver()
    {
        DropZoneBackground = DragOverBrush;
    }

    /// <summary>
    /// ドラッグリーブ時にドロップゾーンの背景をデフォルトに戻す
    /// </summary>
    public void SetDropZoneDefault()
    {
        DropZoneBackground = DefaultDropZoneBrush;
    }

    /// <summary>
    /// 処理中表示にドロップゾーンの背景を更新する
    /// </summary>
    public void SetDropZoneProcessing()
    {
        DropZoneBackground = ProcessingBrush;
    }

    /// <summary>
    /// ドロップされたパスを処理する
    /// </summary>
    /// <param name="paths">ドロップされたパスの配列</param>
    public async System.Threading.Tasks.Task ProcessDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (_fileProcessor is null)
        {
            LogMessage("初期化が完了していないのだ。しばらく待つのだ。");
            return;
        }

        ShowProcessing("ファイル変換中...", "ファイルを処理しています...");
        try
        {
            var pathArray = new string[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                pathArray[i] = paths[i];
            }
            await _fileProcessor.ProcessDroppedItemsAsync(pathArray);
        }
        finally
        {
            // 残っているログを全てフラッシュ
            FlushLogBatchFinal();
            HideProcessing();
            SetDropZoneDefault();
        }
    }

    /// <summary>
    /// URLからWebページをスクレイピングしてJSONで出力する
    /// </summary>
    /// <param name="outputDirectory">出力先ディレクトリ</param>
    public async Task ExtractUrlAsync(string outputDirectory)
    {
        var url = UrlInput?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            LogMessage("URLが入力されていないのだ。");
            return;
        }

        if (_webScraperService is null)
        {
            LogMessage("初期化が完了していないのだ。しばらく待つのだ。");
            return;
        }

        ShowProcessing("URL抽出中...", "Webページをスクレイピングしています...");
        try
        {
            // ファイル名をURLから生成（安全な名前に変換）
            var fileName = WebScraperService.GenerateSafeFileName(url);
            var outputPath = System.IO.Path.Combine(outputDirectory, fileName);

            await _webScraperService.ScrapeAsync(url, outputPath);
            LogMessage($"抽出完了なのだ: {outputPath}");
            UrlInput = string.Empty;
        }
        catch (HttpRequestException ex)
        {
            LogMessage($"HTTP通信エラーなのだ: {ex.Message}", LogLevel.Error);
            Logger.LogException("URL抽出中にHTTPエラーが発生しました", ex);
        }
        catch (ArgumentException ex)
        {
            LogMessage($"URL解析エラーなのだ: {ex.Message}", LogLevel.Error);
        }
        catch (Exception ex)
        {
            LogMessage($"URL抽出中にエラーが発生したのだ: {ex.Message}", LogLevel.Error);
            Logger.LogException("URL抽出中にエラーが発生しました", ex);
        }
        finally
        {
            FlushLogBatchFinal();
            HideProcessing();
        }
    }

    /// <summary>
    /// 処理中オーバーレイを表示する
    /// </summary>
    /// <param name="title">表示するタイトル</param>
    /// <param name="status">表示するステータスメッセージ</param>
    private void ShowProcessing(string title, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProcessingTitle = title;
            ProcessingStatus = status;
            IsProcessing = true;
            SetDropZoneProcessing();
        });
    }

    /// <summary>
    /// 処理ステータスを更新する
    /// </summary>
    /// <param name="status">ステータスメッセージ</param>
    private void UpdateProcessingStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProcessingStatus = status;
        });
    }

    /// <summary>
    /// Pythonダウンロード進捗を更新する
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdatePythonDownloadProgress(double progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = progress;
            IsDownloading = progress > 0 && progress < 100;
            
            if (progress > 0 && progress < 100)
            {
                UpdateProcessingStatus("Pythonダウンロード中...");
            }
            else if (progress >= 100)
            {
                UpdateProcessingStatus("Python展開中...");
            }
        });
    }

    /// <summary>
    /// ffmpegダウンロード進捗を更新する
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdateFfmpegDownloadProgress(double progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = progress;
            IsDownloading = progress > 0 && progress < 100;
            
            if (progress > 0 && progress < 100)
            {
                UpdateProcessingStatus("ffmpegダウンロード中...");
            }
            else if (progress >= 100)
            {
                UpdateProcessingStatus("ffmpeg展開中...");
            }
        });
    }

    /// <summary>
    /// Ollamaダウンロード進捗を更新する（プログレスバーのみ担当）
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdateOllamaDownloadProgress(double progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = progress;
            IsDownloading = progress > 0 && progress < 100;
        });
    }

    /// <summary>
    /// Ollamaステータスメッセージを更新する（オーバーレイのステータス文字列を担当）
    /// </summary>
    /// <param name="status">ステータスメッセージ</param>
    private void UpdateOllamaStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProcessingStatus = status;
        });
    }

    /// <summary>
    /// 処理中オーバーレイを非表示にする
    /// </summary>
    private void HideProcessing()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsProcessing = false;
        });
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
    ~MainWindowViewModel()
    {
        Dispose(false);
    }

    /// <summary>
    /// リソースを解放する（内部実装）
    /// </summary>
    /// <param name="disposing">マネージドリソースも解放するかどうか</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _ollamaManager?.Dispose();
                _webScraperService?.Dispose();
            }
            catch (Exception ex)
            {
                try
                {
                    LogMessage($"リソース解放中にエラーなのだ: {ex.Message}", LogLevel.Error);
                }
                catch
                {
                    // ログ出力も失敗する場合は無視
                }
            }
        }
    }
}
