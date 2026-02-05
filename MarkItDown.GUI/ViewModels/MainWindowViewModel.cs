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
    private string _logText = "アプリケーションが起動しました...";
    private IBrush _dropZoneBackground;
    private FileProcessor? _fileProcessor;
    private OllamaManager? _ollamaManager;
    private bool _isProcessing;
    private string _processingTitle = string.Empty;
    private string _processingStatus = string.Empty;
    private double _downloadProgress;
    private bool _isDownloading;

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
    /// コンストラクタ
    /// </summary>
    public MainWindowViewModel()
    {
        _dropZoneBackground = DefaultDropZoneBrush;
        _ = InitializeManagersAsync();
    }

    /// <summary>
    /// マネージャークラスを非同期で初期化する（埋め込みPythonと ffmpeg の検出またはダウンロードを含む）
    /// </summary>
    private async Task InitializeManagersAsync()
    {
        try
        {
            ShowProcessing("MarkItDown.GUI を初期化中...", "Python環境を初期化中...");

            var pythonEnvironmentManager = new PythonEnvironmentManager(LogMessage, UpdatePythonDownloadProgress);
            await pythonEnvironmentManager.InitializeAsync();

            if (!pythonEnvironmentManager.IsPythonAvailable)
            {
                LogMessage("Python環境の初期化に失敗しました。");
                return;
            }

            UpdateProcessingStatus("ffmpeg環境を準備中...");
            var ffmpegManager = new FfmpegManager(LogMessage, UpdateFfmpegDownloadProgress);
            await ffmpegManager.InitializeAsync();

            UpdateProcessingStatus("Ollama環境を確認中...");
            _ollamaManager = new OllamaManager(LogMessage, UpdateOllamaDownloadProgress);
            await _ollamaManager.InitializeAsync();

            var pythonExe = pythonEnvironmentManager.PythonExecutablePath;
            var ffmpegBinPath = ffmpegManager.IsFfmpegAvailable ? ffmpegManager.FfmpegBinPath : null;
            var ollamaUrl = _ollamaManager.IsOllamaAvailable ? _ollamaManager.OllamaUrl : null;
            var ollamaModel = _ollamaManager.IsOllamaAvailable ? _ollamaManager.DefaultModel : null;

            UpdateProcessingStatus("Pythonパッケージを確認中...");
            var pythonPackageManager = new PythonPackageManager(pythonExe, LogMessage);
            pythonPackageManager.InstallMarkItDownPackage();

            UpdateProcessingStatus("MarkItDownライブラリを準備中...");
            var markItDownProcessor = new MarkItDownProcessor(pythonExe, LogMessage, ffmpegBinPath, ollamaUrl, ollamaModel);
            _fileProcessor = new FileProcessor(markItDownProcessor, LogMessage);

            UpdateProcessingStatus("初期化完了");
            LogMessage("すべての初期化が完了しました。");
        }
        catch (Exception ex)
        {
            LogMessage($"初期化中にエラーが発生しました: {ex.Message}");
            Logger.LogException("初期化中にエラーが発生しました", ex);
        }
        finally
        {
            await Task.Delay(500);
            HideProcessing();
        }
    }

    /// <summary>
    /// ログメッセージを画面に追加する
    /// </summary>
    /// <param name="message">表示するメッセージ</param>
    public void LogMessage(string message)
    {
        try
        {
            Logger.Log(message, LogLevel.Info);
            
            Dispatcher.UIThread.Post(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                LogText += $"[{timestamp}] {message}\n";
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to log message: {ex.Message}");
            Logger.LogException("ログメッセージの追加に失敗しました", ex);
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
            LogMessage("初期化が完了していません。しばらくお待ちください。");
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
            HideProcessing();
            SetDropZoneDefault();
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

    private bool _isOllamaExtracting;
    private bool _ollamaDownloadCompleted;

    /// <summary>
    /// Ollamaダウンロード進捗を更新する
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdateOllamaDownloadProgress(double progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = progress;
            IsDownloading = progress > 0 && progress < 100;
            
            if (progress >= 100 && !_ollamaDownloadCompleted)
            {
                _ollamaDownloadCompleted = true;
                _isOllamaExtracting = true;
            }
            
            if (_isOllamaExtracting)
            {
                if (progress == 0)
                {
                    UpdateProcessingStatus("Ollamaアーカイブを展開準備中...");
                }
                else if (progress > 0 && progress < 100)
                {
                    UpdateProcessingStatus($"Ollamaファイルを展開中... {progress:F0}%");
                }
                else if (progress >= 100)
                {
                    UpdateProcessingStatus("Ollama展開完了");
                    _isOllamaExtracting = false;
                }
            }
            else
            {
                if (progress > 0 && progress < 100)
                {
                    UpdateProcessingStatus("Ollamaダウンロード中...");
                }
            }
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
            }
            catch (Exception ex)
            {
                try
                {
                    LogMessage($"リソース解放中にエラー: {ex.Message}");
                }
                catch
                {
                    // ログ出力も失敗する場合は無視
                }
            }
        }
    }
}
