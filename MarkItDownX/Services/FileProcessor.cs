using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MarkItDownX.Services;

/// <summary>
/// Responsible for file processing
/// </summary>
public class FileProcessor
{
    private readonly MarkItDownProcessor _markItDownProcessor;
    private readonly Action<string> _logMessage;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="markItDownProcessor">MarkItDown processor class</param>
    /// <param name="logMessage">Log output function</param>
    public FileProcessor(MarkItDownProcessor markItDownProcessor, Action<string> logMessage)
    {
        _markItDownProcessor = markItDownProcessor;
        _logMessage = logMessage;
    }

    /// <summary>
    /// Validate file path to prevent path traversal attacks
    /// </summary>
    /// <param name="path">Path to validate</param>
    /// <param name="fullPath">Canonical path when valid</param>
    /// <returns>True if path is valid and safe</returns>
    private bool TryGetValidPath(string path, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);

            if (!Path.IsPathRooted(fullPath))
            {
                return false;
            }

            // Verify the path exists and is accessible
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Process dropped items
    /// </summary>
    /// <param name="paths">Array of dropped paths</param>
    public async Task ProcessDroppedItemsAsync(string[] paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            // Validate path for security
            if (!TryGetValidPath(path, out var fullPath))
            {
                _logMessage($"Invalid path rejected: {path}");
                continue;
            }

            if (File.Exists(fullPath))
            {
                files.Add(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                folders.Add(fullPath);
            }
        }

        if (files.Count > 0 || folders.Count > 0)
        {
            await ProcessFilesWithMarkItDownAsync(new List<string>(files), new List<string>(folders));
        }
    }

    /// <summary>
    /// Process files and folders using MarkItDown
    /// </summary>
    /// <param name="files">List of files to process</param>
    /// <param name="folders">List of folders to process</param>
    private async Task ProcessFilesWithMarkItDownAsync(IReadOnlyCollection<string> files, IReadOnlyCollection<string> folders)
    {
        string? tempFilePathsJson = null;
        string? tempFolderPathsJson = null;

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
            var tempDirectory = Path.GetTempPath();
            tempFilePathsJson = Path.Combine(tempDirectory, $"markitdown_files_{Guid.NewGuid():N}.json");
            tempFolderPathsJson = Path.Combine(tempDirectory, $"markitdown_folders_{Guid.NewGuid():N}.json");
                
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
        finally
        {
            CleanupTempFile(tempFilePathsJson);
            CleanupTempFile(tempFolderPathsJson);
        }
    }

    private void CleanupTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logMessage($"一時ファイルを削除しました: {path}");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"一時ファイル削除に失敗: {ex.Message}");
        }
    }
}
