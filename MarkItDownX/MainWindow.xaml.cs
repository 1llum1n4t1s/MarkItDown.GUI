using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkItDownX.Services;

namespace MarkItDownX;

    /// <summary>
/// Interaction logic for MainWindow.xaml
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
    /// Initialize manager classes
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
    /// Initialize drop zone
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
    /// Drag over event handler
        /// </summary>
    /// <param name="sender">Event source</param>
    /// <param name="e">Drag event arguments</param>
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
    /// Drag enter event handler
        /// </summary>
    /// <param name="sender">Event source</param>
    /// <param name="e">Drag event arguments</param>
    private void DropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
            DropZone.Background = (SolidColorBrush)FindResource("DragOverBrush");
            }
            e.Handled = true;
        }

        /// <summary>
    /// Drag leave event handler
        /// </summary>
    /// <param name="sender">Event source</param>
    /// <param name="e">Drag event arguments</param>
    private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
        {
        DropZone.Background = (SolidColorBrush)FindResource("DefaultDropZoneBrush");
            e.Handled = true;
        }

        /// <summary>
    /// Drop event handler
        /// </summary>
    /// <param name="sender">Event source</param>
    /// <param name="e">Drag event arguments</param>
    private async void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
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

        DropZone.Background = (SolidColorBrush)FindResource("DefaultDropZoneBrush");
            e.Handled = true;
        }

        /// <summary>
    /// Show progress bar
    /// </summary>
    /// <param name="message">Message to display</param>
        private void ShowProgress(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = message;
                ProgressGrid.Visibility = Visibility.Visible;

                // Change drop zone background to indicate processing
                DropZone.Background = (SolidColorBrush)FindResource("ProcessingBrush");
            });
        }

    /// <summary>
    /// Hide progress bar
    /// </summary>
        private void HideProgress()
        {
            Dispatcher.Invoke(() =>
            {
                ProgressGrid.Visibility = Visibility.Collapsed;

                // Restore drop zone background
                DropZone.Background = (SolidColorBrush)FindResource("DefaultDropZoneBrush");
            });
    }

        /// <summary>
    /// Display log message on screen
        /// </summary>
    /// <param name="message">Message to display</param>
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
            catch (Exception ex)
            {
                // Log error if UI thread operation fails
                System.Diagnostics.Debug.WriteLine($"Failed to log message: {ex.Message}");
        }
    }
}