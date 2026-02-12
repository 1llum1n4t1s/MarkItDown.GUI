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
    private string _logText = "アプリケーションが起動したのだ...\n";
    private IBrush _dropZoneBackground;
    private FileProcessor? _fileProcessor;
    private ClaudeCodeSetupService? _claudeSetupService;
    private WebScraperService? _webScraperService;
    private bool _isClaudeEnabled;
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

    // 並列ダウンロード進捗の個別追跡（スレッドセーフにするため lock で保護）
    private readonly object _progressLock = new();
    private double _pythonProgress;
    private double _ffmpegProgress;
    private double _claudeProgress;
    private string _pythonStatus = string.Empty;
    private string _ffmpegStatus = string.Empty;
    private string _claudeStatus = string.Empty;
    // 現在アクティブなダウンロードタスク数（0 なら IsDownloading=false）
    private int _activeDownloadCount;

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
    /// Claude AI を使用するかどうか
    /// </summary>
    public bool IsClaudeEnabled
    {
        get => _isClaudeEnabled;
        set
        {
            if (SetProperty(ref _isClaudeEnabled, value))
            {
                AppSettings.SetUseClaudeAI(value);
                if (value)
                {
                    _ = SetupClaudeAsync();
                }
            }
        }
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
            // 設定ファイルを最初に読み込み
            AppSettings.LoadSettings();
            _isClaudeEnabled = AppSettings.GetUseClaudeAI();
            OnPropertyChanged(nameof(IsClaudeEnabled));

            ShowProcessing("MarkItDown.GUI を初期化中...", "Python環境を初期化中...");

            var pythonEnvironmentManager = new PythonEnvironmentManager(LogMessage, UpdatePythonDownloadProgress, logError: LogError, logWarning: LogWarning);
            await pythonEnvironmentManager.InitializeAsync();

            if (!pythonEnvironmentManager.IsPythonAvailable)
            {
                LogMessage("Python環境の初期化に失敗したのだ。", LogLevel.Error);
                return;
            }

            // ffmpeg とパッケージインストールは互いに独立しているため並列実行
            UpdateProcessingStatus("ffmpeg / パッケージを並列で準備中...");

            var ffmpegManager = new FfmpegManager(LogMessage, UpdateFfmpegDownloadProgress, logError: LogError, logWarning: LogWarning);
            var pythonExe = pythonEnvironmentManager.PythonExecutablePath;
            var pythonPackageManager = new PythonPackageManager(pythonExe, LogMessage, logError: LogError, logWarning: LogWarning);

            // 並列ダウンロードフェーズ: ffmpeg の進捗を表示する
            lock (_progressLock)
            {
                _activeDownloadCount = 1;
                _pythonProgress = 0;
                _pythonStatus = string.Empty;
            }

            await Task.WhenAll(
                ffmpegManager.InitializeAsync(),
                pythonPackageManager.InstallMarkItDownPackageAsync());

            // 並列ダウンロード完了: 集約モードを解除
            lock (_progressLock)
            {
                _activeDownloadCount = 0;
            }

            var ffmpegBinPath = ffmpegManager.IsFfmpegAvailable ? ffmpegManager.FfmpegBinPath : null;

            UpdateProcessingStatus("MarkItDownライブラリを準備中...");
            var markItDownProcessor = new MarkItDownProcessor(pythonExe, LogMessage, ffmpegBinPath, logError: LogError);
            _fileProcessor = new FileProcessor(markItDownProcessor, LogMessage, logError: LogError);

            _webScraperService = new WebScraperService(LogMessage, UpdateProcessingStatus, logError: LogError);
            var playwrightScraper = new PlaywrightScraperService(pythonExe, LogMessage, UpdateProcessingStatus, logError: LogError);

            // Instagram ログインコールバックを設定
            playwrightScraper.InstagramLoginCallback = PromptInstagramLoginAsync;
            playwrightScraper.Instagram2FACallback = PromptInstagram2FAAsync;

            _webScraperService.SetPlaywrightScraper(playwrightScraper);

            // 保存された設定で Claude が有効な場合、セットアップを実行
            if (_isClaudeEnabled)
            {
                await SetupClaudeAsync();
            }

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
    /// Claude Code CLI のセットアップ・認証を実行する
    /// </summary>
    private async Task SetupClaudeAsync()
    {
        try
        {
            LogMessage("Claude AI セットアップを開始するのだ...");
            _claudeSetupService = new ClaudeCodeSetupService(LogMessage, UpdateClaudeDownloadProgress, UpdateClaudeStatus);

            // Node.js のインストール
            UpdateClaudeStatus("Node.js を準備中...");
            await _claudeSetupService.EnsureNodeJsAsync();

            if (!_claudeSetupService.IsNodeJsInstalled)
            {
                LogMessage("Node.js のインストールに失敗したのだ。", LogLevel.Error);
                return;
            }

            // Claude Code CLI のインストール
            UpdateClaudeStatus("Claude Code CLI を準備中...");
            await _claudeSetupService.EnsureCliAsync();

            if (!_claudeSetupService.IsCliInstalled)
            {
                LogMessage("Claude Code CLI のインストールに失敗したのだ。", LogLevel.Error);
                return;
            }

            // ログインチェック
            UpdateClaudeStatus("Claude 認証を確認中...");
            var isLoggedIn = await _claudeSetupService.IsLoggedInAsync();

            if (!isLoggedIn)
            {
                LogMessage("Claude にログインしていないのだ。ログイン画面を起動するのだ...");
                await _claudeSetupService.RunLoginAsync();
                isLoggedIn = await _claudeSetupService.IsLoggedInAsync();
            }

            if (isLoggedIn)
            {
                // 接続検証
                UpdateClaudeStatus("Claude 接続を検証中...");
                var isConnected = await _claudeSetupService.VerifyConnectivityAsync();

                if (isConnected)
                {
                    var nodePath = _claudeSetupService.GetNodePath();
                    var cliJsPath = _claudeSetupService.GetCliJsPath();

                    // WebScraperService と PlaywrightScraperService に Claude 設定を渡す
                    _webScraperService?.SetClaudeConfig(nodePath, cliJsPath);
                    _webScraperService?.GetPlaywrightScraper()?.SetClaudeConfig(nodePath, cliJsPath);

                    LogMessage("Claude AI セットアップ完了なのだ！");
                }
                else
                {
                    LogMessage("Claude 接続検証に失敗したのだ。AI機能なしで動作するのだ。", LogLevel.Warning);
                }
            }
            else
            {
                LogMessage("Claude ログインがキャンセルされたのだ。AI機能なしで動作するのだ。", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Claude セットアップ中にエラーなのだ: {ex.Message}", LogLevel.Error);
            Logger.LogException("Claude セットアップ中にエラーが発生しました", ex);
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
        var batchLineCount = _logBatch.Count;
        var sb = new System.Text.StringBuilder(batchLineCount * 50);
        foreach (var msg in _logBatch)
        {
            sb.AppendLine(msg);
        }
        var batchContent = sb.ToString();
        _logBatch.Clear();

        Dispatcher.UIThread.Post(() =>
        {
            _logLineCount += batchLineCount;

            // ログ行数上限超過時は古いログを削除
            // Split + Join の代わりに IndexOf で N番目の改行位置を探して Substring する
            if (_logLineCount > MaxLogLines)
            {
                var linesToRemove = _logLineCount - MaxLogLines;
                var text = LogText;
                var pos = 0;
                for (var i = 0; i < linesToRemove && pos < text.Length; i++)
                {
                    var next = text.IndexOf('\n', pos);
                    if (next < 0) break;
                    pos = next + 1;
                }
                LogText = text[pos..] + batchContent;
                _logLineCount = MaxLogLines;
            }
            else
            {
                LogText += batchContent;
            }
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

        if (_isProcessing)
        {
            LogMessage("別の処理が実行中なのだ。完了するまで待つのだ。");
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

        if (_isProcessing)
        {
            LogMessage("別の処理が実行中なのだ。完了するまで待つのだ。");
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
            // X.com ユーザーページの場合はサブフォルダを作成
            string outputPath;
            var normalizedUrl = url;
            if (!normalizedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !normalizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalizedUrl = "https://" + normalizedUrl;
            }

            if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();
                if ((host is "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com"
                     or "mobile.twitter.com" or "mobile.x.com")
                    && WebScraperService.TryExtractXTwitterUsername(normalizedUrl, out var username))
                {
                    // 重複フォルダ回避: 同名フォルダが既に存在する場合は _1, _2, ... を付与
                    var baseDirName = username;
                    var userDir = System.IO.Path.Combine(outputDirectory, baseDirName);
                    var suffix = 1;
                    while (System.IO.Directory.Exists(userDir))
                    {
                        baseDirName = $"{username}_{suffix}";
                        userDir = System.IO.Path.Combine(outputDirectory, baseDirName);
                        suffix++;
                    }
                    System.IO.Directory.CreateDirectory(userDir);
                    outputPath = System.IO.Path.Combine(userDir, $"{username}.json");
                    LogMessage($"X.com ユーザーフォルダを作成したのだ: {userDir}");
                }
                else if (host is "instagram.com" or "www.instagram.com" or "m.instagram.com"
                          || host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase))
                {
                    // Instagram: ユーザーURLの場合はユーザーフォルダを作成
                    // 投稿/リールURLの場合はinstagram_postフォルダを作成
                    string folderName;
                    if (WebScraperService.TryExtractInstagramUsername(normalizedUrl, out var igUsername))
                    {
                        folderName = igUsername;
                    }
                    else
                    {
                        // 投稿/リールURL: shortcodeからフォルダ名を生成
                        var pathSegments = uri.AbsolutePath.Trim('/').Split('/');
                        folderName = pathSegments.Length >= 2 ? $"instagram_{pathSegments[1]}" : "instagram_post";
                    }

                    var baseDirName = folderName;
                    var userDir = System.IO.Path.Combine(outputDirectory, baseDirName);
                    var suffix = 1;
                    while (System.IO.Directory.Exists(userDir))
                    {
                        baseDirName = $"{folderName}_{suffix}";
                        userDir = System.IO.Path.Combine(outputDirectory, baseDirName);
                        suffix++;
                    }
                    System.IO.Directory.CreateDirectory(userDir);
                    outputPath = System.IO.Path.Combine(userDir, $"{folderName}.json");
                    LogMessage($"Instagram フォルダを作成したのだ: {userDir}");
                }
                else
                {
                    var fileName = WebScraperService.GenerateSafeFileName(url);
                    outputPath = System.IO.Path.Combine(outputDirectory, fileName);
                }
            }
            else
            {
                var fileName = WebScraperService.GenerateSafeFileName(url);
                outputPath = System.IO.Path.Combine(outputDirectory, fileName);
            }

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
        lock (_progressLock)
        {
            _pythonProgress = progress;
            if (progress > 0 && progress < 100)
                _pythonStatus = "Pythonダウンロード中...";
            else if (progress >= 100)
                _pythonStatus = "Python展開中...";
            else
                _pythonStatus = string.Empty;
        }
        UpdateAggregatedProgress();
    }

    /// <summary>
    /// ffmpegダウンロード進捗を更新する
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdateFfmpegDownloadProgress(double progress)
    {
        lock (_progressLock)
        {
            _ffmpegProgress = progress;
            if (progress > 0 && progress < 100)
                _ffmpegStatus = "ffmpegダウンロード中...";
            else if (progress >= 100)
                _ffmpegStatus = "ffmpeg展開中...";
            else
                _ffmpegStatus = string.Empty;
        }
        UpdateAggregatedProgress();
    }

    /// <summary>
    /// Claudeダウンロード進捗を更新する（プログレスバーのみ担当）
    /// </summary>
    /// <param name="progress">進捗率（0～100）</param>
    private void UpdateClaudeDownloadProgress(double progress)
    {
        lock (_progressLock)
        {
            _claudeProgress = progress;
        }
        UpdateAggregatedProgress();
    }

    /// <summary>
    /// Claudeステータスメッセージを更新する（オーバーレイのステータス文字列を担当）
    /// </summary>
    /// <param name="status">ステータスメッセージ</param>
    private void UpdateClaudeStatus(string status)
    {
        lock (_progressLock)
        {
            _claudeStatus = status;
        }
        UpdateAggregatedProgress();
    }

    /// <summary>
    /// 並列ダウンロードの個別進捗を集約し、UIプロパティに反映する。
    /// アクティブなダウンロード（0 &lt; progress &lt; 100）の加重平均を計算する。
    /// </summary>
    private void UpdateAggregatedProgress()
    {
        double aggregated;
        bool anyDownloading;
        string statusMessage;

        lock (_progressLock)
        {
            // アクティブなダウンロードのみを対象に加重平均を計算
            // _activeDownloadCount は InitializeManagersAsync で設定される
            var count = _activeDownloadCount;
            if (count <= 0)
            {
                // 並列ダウンロード前（Pythonのみの段階）はそのまま使う
                aggregated = _pythonProgress;
                anyDownloading = _pythonProgress > 0 && _pythonProgress < 100;
                statusMessage = _pythonStatus;
            }
            else
            {
                // ffmpeg と Claude の進捗を均等に加重平均（未開始=0, 完了=100 で計算）
                aggregated = (_ffmpegProgress + _claudeProgress) / count;
                anyDownloading = (_ffmpegProgress > 0 && _ffmpegProgress < 100)
                              || (_claudeProgress > 0 && _claudeProgress < 100);

                // ステータスメッセージ: アクティブなダウンロードの状況を結合表示
                var parts = new List<string>(2);
                if (!string.IsNullOrEmpty(_ffmpegStatus)) parts.Add(_ffmpegStatus.TrimEnd('.'));
                if (!string.IsNullOrEmpty(_claudeStatus)) parts.Add(_claudeStatus.TrimEnd('.'));
                statusMessage = parts.Count > 0 ? string.Join(" / ", parts) : "準備中...";
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = aggregated;
            IsDownloading = anyDownloading;
            if (anyDownloading)
            {
                ProcessingStatus = statusMessage;
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

    // ────────────────────────────────────────────
    //  Instagram ログインダイアログ
    // ────────────────────────────────────────────

    /// <summary>
    /// Instagram のログイン情報をUIスレッドでユーザーに入力させる
    /// </summary>
    private async Task<(string Username, string Password)?> PromptInstagramLoginAsync(string message)
    {
        var tcs = new TaskCompletionSource<(string, string)?>();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var dialog = new InstagramLoginDialog();
                dialog.SetMessage(message);
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null;

                if (mainWindow is not null)
                {
                    var result = await dialog.ShowDialog<(string, string)?>(mainWindow);
                    tcs.TrySetResult(result);
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                LogError($"ログインダイアログでエラー: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Instagram の2FAコードをUIスレッドでユーザーに入力させる
    /// </summary>
    private async Task<string?> PromptInstagram2FAAsync(string message)
    {
        var tcs = new TaskCompletionSource<string?>();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var dialog = new Instagram2FADialog();
                dialog.SetMessage(message);
                var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null;

                if (mainWindow is not null)
                {
                    var result = await dialog.ShowDialog<string?>(mainWindow);
                    tcs.TrySetResult(result);
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                LogError($"2FAダイアログでエラー: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        return await tcs.Task;
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
