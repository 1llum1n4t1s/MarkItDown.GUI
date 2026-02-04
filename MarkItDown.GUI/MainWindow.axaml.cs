using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using MarkItDown.GUI.ViewModels;

namespace MarkItDown.GUI;

/// <summary>
/// メインウィンドウのコードビハインド
/// </summary>
public partial class MainWindow : Avalonia.Controls.Window
{
    /// <summary>
    /// コンストラクタ
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        InitializeDropZone();
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// ViewModel を取得する（null でない前提）
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// ウィンドウが閉じられたときのハンドラー
    /// </summary>
    /// <param name="sender">イベントソース</param>
    /// <param name="e">イベント引数</param>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ViewModel.Dispose();
    }

    /// <summary>
    /// ドロップゾーンのイベントハンドラーを設定する
    /// </summary>
    private void InitializeDropZone()
    {
        DragDrop.SetAllowDrop(DropZoneBorder, true);
        DropZoneBorder.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
        DropZoneBorder.AddHandler(DragDrop.DragLeaveEvent, DropZone_DragLeave);
        DropZoneBorder.AddHandler(DragDrop.DropEvent, DropZone_Drop);
    }

    /// <summary>
    /// ドラッグオーバー時のハンドラー
    /// </summary>
    /// <param name="sender">イベントソース</param>
    /// <param name="e">ドラッグイベント引数</param>
    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            ViewModel.SetDropZoneDragOver();
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// ドラッグリーブ時のハンドラー
    /// </summary>
    /// <param name="sender">イベントソース</param>
    /// <param name="e">ルーティングイベント引数</param>
    private void DropZone_DragLeave(object? sender, RoutedEventArgs e)
    {
        ViewModel.SetDropZoneDefault();
        e.Handled = true;
    }

    /// <summary>
    /// ドロップ時のハンドラー
    /// </summary>
    /// <param name="sender">イベントソース</param>
    /// <param name="e">ドラッグイベント引数</param>
    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        var paths = files?.Select(f => f.Path?.LocalPath ?? f.Name).Where(p => p is not null).Cast<string>().ToList();
        if (paths is not null && paths.Count > 0)
        {
            await ViewModel.ProcessDroppedPathsAsync(paths);
        }

        ViewModel.SetDropZoneDefault();
        e.Handled = true;
    }
}
