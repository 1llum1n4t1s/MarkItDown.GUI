using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkItDownX.Services;

namespace MarkItDownX;

    /// <summary>
/// MainWindow.xaml の相互作用ロジックなのだ
    /// </summary>
    public partial class MainWindow : Window
    {
    private PythonEnvironmentManager? _pythonEnvironmentManager;
    private PythonPackageManager? _pythonPackageManager;
    private MarkItDownProcessor? _markItDownProcessor;
    private FileProcessor? _fileProcessor;

        public MainWindow()
        {
            InitializeComponent();
            InitializeDropZone();
        InitializeManagers();
    }

    /// <summary>
    /// 各マネージャークラスを初期化するのだ
    /// </summary>
    private void InitializeManagers()
    {
        // Python環境マネージャーを初期化
        _pythonEnvironmentManager = new PythonEnvironmentManager(LogMessage);
        _pythonEnvironmentManager.Initialize();

        // Pythonが利用可能な場合のみ、パッケージマネージャーとMarkItDownプロセッサーを初期化
        if (_pythonEnvironmentManager.IsPythonAvailable)
        {
            _pythonPackageManager = new PythonPackageManager(_pythonEnvironmentManager.PythonExecutablePath, LogMessage);
            _markItDownProcessor = new MarkItDownProcessor(_pythonEnvironmentManager.PythonExecutablePath, LogMessage);
            _fileProcessor = new FileProcessor(_markItDownProcessor, LogMessage);

            // markitdownパッケージを自動インストールするのだ
            _pythonPackageManager.InstallMarkItDownPackage();
        }
    }

    /// <summary>
    /// ドロップゾーンの初期化を行うのだ
        /// </summary>
        private void InitializeDropZone()
        {
        // ドロップゾーンのイベントハンドラーを設定
            DropZone.AllowDrop = true;
            DropZone.DragOver += DropZone_DragOver;
            DropZone.DragEnter += DropZone_DragEnter;
            DropZone.DragLeave += DropZone_DragLeave;
            DropZone.Drop += DropZone_Drop;
        }

        /// <summary>
    /// ドラッグオーバーイベントハンドラーなのだ
        /// </summary>
    /// <param name="sender">イベントソースなのだ</param>
    /// <param name="e">ドラッグイベント引数なのだ</param>
    private void DropZone_DragOver(object sender, System.Windows.DragEventArgs e)
        {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
            e.Effects = System.Windows.DragDropEffects.Copy;
            }
            else
            {
            e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        /// <summary>
    /// ドラッグエンターイベントハンドラーなのだ
        /// </summary>
    /// <param name="sender">イベントソースなのだ</param>
    /// <param name="e">ドラッグイベント引数なのだ</param>
    private void DropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
            DropZone.Background = new SolidColorBrush(Colors.LightBlue);
            }
            e.Handled = true;
        }

        /// <summary>
    /// ドラッグリーブイベントハンドラーなのだ
        /// </summary>
    /// <param name="sender">イベントソースなのだ</param>
    /// <param name="e">ドラッグイベント引数なのだ</param>
    private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
        DropZone.Background = new SolidColorBrush(Colors.LightGray);
            e.Handled = true;
        }

        /// <summary>
    /// ドロップイベントハンドラーなのだ
        /// </summary>
    /// <param name="sender">イベントソースなのだ</param>
    /// <param name="e">ドラッグイベント引数なのだ</param>
    private async void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (_fileProcessor != null)
            {
                // プログレスバーを表示
                ShowProgress("ファイル変換処理中...");
                
                try
                {
                    await _fileProcessor.ProcessDroppedItemsAsync(paths);
                }
                finally
                {
                    // プログレスバーを非表示
                    HideProgress();
                }
            }
        }
            
        DropZone.Background = new SolidColorBrush(Colors.LightGray);
            e.Handled = true;
        }

        /// <summary>
    /// プログレスバーを表示するのだ
        /// </summary>
    /// <param name="message">表示するメッセージなのだ</param>
        private void ShowProgress(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = message;
                ProgressGrid.Visibility = Visibility.Visible;
                
                // ドロップゾーンの背景を変更して処理中であることを示す
                DropZone.Background = new SolidColorBrush(Colors.LightYellow);
            });
        }

    /// <summary>
    /// プログレスバーを非表示にするのだ
    /// </summary>
        private void HideProgress()
        {
            Dispatcher.Invoke(() =>
            {
                ProgressGrid.Visibility = Visibility.Collapsed;
                
                // ドロップゾーンの背景を元に戻す
                DropZone.Background = new SolidColorBrush(Colors.LightGray);
            });
    }

        /// <summary>
    /// ログを画面に表示するのだ
        /// </summary>
    /// <param name="message">表示するメッセージなのだ</param>
        private void LogMessage(string message)
        {
            try
            {
                // UIスレッドで実行
                Dispatcher.Invoke(() =>
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    var logEntry = $"[{timestamp}] {message}\n";
                    LogTextBox.AppendText(logEntry);
                    
                    // 自動スクロール
                    LogTextBox.ScrollToEnd();
                });
            }
            catch
            {
                // UIスレッドでエラーが発生した場合はコンソールに出力
                Console.WriteLine(message);
        }
    }
}