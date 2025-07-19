using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MarkItDownX.Services;

/// <summary>
/// ファイル処理を担当するクラスなのだ
/// </summary>
public class FileProcessor
{
    private readonly MarkItDownProcessor _markItDownProcessor;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// コンストラクタなのだ
    /// </summary>
    /// <param name="markItDownProcessor">MarkItDown処理クラスなのだ</param>
    /// <param name="logMessage">ログ出力関数なのだ</param>
    public FileProcessor(MarkItDownProcessor markItDownProcessor, Action<string> logMessage)
    {
        _markItDownProcessor = markItDownProcessor;
        _logMessage = logMessage;
    }

    /// <summary>
    /// ドロップされたアイテムを処理するのだ
    /// </summary>
    /// <param name="paths">ドロップされたパスの配列なのだ</param>
    public async Task ProcessDroppedItemsAsync(string[] paths)
    {
        var files = new List<string>();
        var folders = new List<string>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                folders.Add(path);
            }
        }

        if (files.Count > 0 || folders.Count > 0)
        {
            await ProcessFilesWithMarkItDownAsync(files, folders);
        }
    }

    /// <summary>
    /// MarkItDownを使用してファイルとフォルダを処理するのだ
    /// </summary>
    /// <param name="files">処理するファイルのリストなのだ</param>
    /// <param name="folders">処理するフォルダのリストなのだ</param>
    private async Task ProcessFilesWithMarkItDownAsync(List<string> files, List<string> folders)
    {
        try
        {
            // MarkItDownライブラリの利用可能性を事前にチェック
            _logMessage("MarkItDownライブラリの利用可能性をチェック中...");
            if (!_markItDownProcessor.CheckMarkItDownAvailability())
            {
                _logMessage("MarkItDownライブラリが利用できませんでした。処理を中止します。");
                return;
            }
            _logMessage("MarkItDownライブラリが利用可能です。処理を開始します。");

            // デバッグ情報を表示
            _logMessage($"処理開始: ファイル {files.Count}個, フォルダ {folders.Count}個");
            foreach (var file in files)
            {
                _logMessage($"処理対象ファイル: {file}");
            }
            foreach (var folder in folders)
            {
                _logMessage($"処理対象フォルダ: {folder}");
            }

            // アプリケーションディレクトリを取得
            var appDir = Directory.GetCurrentDirectory();
            _logMessage($"C#側アプリケーションディレクトリ: {appDir}");
                
            // ファイルとフォルダのパスを設定
            var filePathsJson = JsonConvert.SerializeObject(files);
            var folderPathsJson = JsonConvert.SerializeObject(folders);
                
            _logMessage($"ファイルパスJSON: {filePathsJson}");
            _logMessage($"フォルダパスJSON: {folderPathsJson}");
                
            // JSON文字列をファイルに保存して、ファイルパスを渡す
            var tempFilePathsJson = Path.Combine(appDir, "temp_file_paths.json");
            var tempFolderPathsJson = Path.Combine(appDir, "temp_folder_paths.json");
                
            // BOMなしのUTF-8でファイルを保存
            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(tempFilePathsJson, filePathsJson, utf8NoBom);
            File.WriteAllText(tempFolderPathsJson, folderPathsJson, utf8NoBom);
                
            _logMessage($"一時ファイルパス: {tempFilePathsJson}");
            _logMessage($"一時フォルダパス: {tempFolderPathsJson}");
                
            // Pythonスクリプトを実行
            await Task.Run(() => _markItDownProcessor.ExecuteMarkItDownConvertScript(appDir, tempFilePathsJson, tempFolderPathsJson));
        }
        catch (Exception ex)
        {
            _logMessage($"MarkItDown変換中にエラーが発生: {ex.Message}");
            _logMessage($"スタックトレース: {ex.StackTrace}");
            _logMessage($"MarkItDown変換中にエラーが発生しました: {ex.Message}");
        }
    }
} 