using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Responsible for file processing
/// </summary>
public class FileProcessor
{
    private readonly MarkItDownProcessor _markItDownProcessor;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly ConcurrentDictionary<string, (long ticks, DateTime timestamp)> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    private enum PathType
    {
        None,
        File,
        Directory
    }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="markItDownProcessor">MarkItDown processor class</param>
    /// <param name="logMessage">Log output function</param>
    public FileProcessor(MarkItDownProcessor markItDownProcessor, Action<string> logMessage, Action<string>? logError = null)
    {
        _markItDownProcessor = markItDownProcessor;
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
    }

    /// <summary>
    /// Validate file path to prevent path traversal attacks
    /// </summary>
    /// <param name="path">Path to validate</param>
    /// <param name="fullPath">Canonical path when valid</param>
    /// <returns>Detected path type when valid</returns>
    private PathType TryGetValidPath(string path, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return PathType.None;
        }

        try
        {
            fullPath = Path.GetFullPath(path);

            if (!Path.IsPathRooted(fullPath))
            {
                return PathType.None;
            }

            if (File.Exists(fullPath))
            {
                return PathType.File;
            }

            if (Directory.Exists(fullPath))
            {
                return PathType.Directory;
            }

            return PathType.None;
        }
        catch
        {
            return PathType.None;
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
            switch (TryGetValidPath(path, out var fullPath))
            {
                case PathType.File:
                    files.Add(fullPath);
                    break;
                case PathType.Directory:
                    folders.Add(fullPath);
                    break;
                default:
                    _logMessage($"無効なパスを拒否したのだ: {path}");
                    break;
            }
        }

        if (files.Count > 0 || folders.Count > 0)
        {
            await ProcessFilesWithMarkItDownAsync(new List<string>(files), new List<string>(folders));
        }
    }

    /// <summary>
    /// Process files and folders using MarkItDown (with parallel execution support)
    /// </summary>
    /// <param name="files">List of files to process</param>
    /// <param name="folders">List of folders to process</param>
    private async Task ProcessFilesWithMarkItDownAsync(IReadOnlyCollection<string> files, IReadOnlyCollection<string> folders)
    {
        try
        {
            // MarkItDownライブラリの利用可能性を事前にチェック
            _logMessage("MarkItDownライブラリの利用可能性をチェック中なのだ...");
            if (!_markItDownProcessor.CheckMarkItDownAvailability())
            {
                _logMessage("MarkItDownライブラリが利用できなかったのだ。処理を中止するのだ。");
                return;
            }
            _logMessage("MarkItDownライブラリが利用可能なのだ。処理を開始するのだ！");

            // デバッグ情報を表示
            _logMessage($"処理開始なのだ: ファイル {files.Count}個, フォルダ {folders.Count}個");
            foreach (var file in files)
            {
                _logMessage($"処理対象ファイルなのだ: {file}");
            }
            foreach (var folder in folders)
            {
                _logMessage($"処理対象フォルダなのだ: {folder}");
            }

            // アプリケーションディレクトリを取得
            var appDir = Directory.GetCurrentDirectory();
            _logMessage($"C#側アプリケーションディレクトリなのだ: {appDir}");

            // ファイル・フォルダを並列処理
            // 最大3タスク同時実行で制限
            const int maxParallelTasks = 3;

            // ファイルを並列処理（メモリ効率的なバッチ処理）
            if (files.Count > 0)
            {
                var fileArray = files.ToArray();
                for (int i = 0; i < fileArray.Length; i += maxParallelTasks)
                {
                    int batchSize = Math.Min(maxParallelTasks, fileArray.Length - i);
                    var batchTasks = new Task[batchSize];
                    for (int j = 0; j < batchSize; j++)
                    {
                        batchTasks[j] = ProcessSingleFileAsync(appDir, fileArray[i + j]);
                    }
                    await Task.WhenAll(batchTasks);
                }
            }

            // フォルダを並列処理（メモリ効率的なバッチ処理）
            if (folders.Count > 0)
            {
                var folderArray = folders.ToArray();
                for (int i = 0; i < folderArray.Length; i += maxParallelTasks)
                {
                    int batchSize = Math.Min(maxParallelTasks, folderArray.Length - i);
                    var batchTasks = new Task[batchSize];
                    for (int j = 0; j < batchSize; j++)
                    {
                        batchTasks[j] = ProcessSingleFolderAsync(appDir, folderArray[i + j]);
                    }
                    await Task.WhenAll(batchTasks);
                }
            }

            _logMessage("すべてのファイル・フォルダ処理が完了したのだ！");
        }
        catch (Exception ex)
        {
            _logError($"MarkItDown変換中にエラーが発生したのだ: {ex.Message}");
            _logMessage($"スタックトレースなのだ: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Process a single file using MarkItDown
    /// </summary>
    /// <param name="appDir">Application directory</param>
    /// <param name="filePath">File path to process</param>
    private async Task ProcessSingleFileAsync(string appDir, string filePath)
    {
        string? tempFilePathsJson = null;
        string? tempFolderPathsJson = null;

        try
        {
            _logMessage($"ファイル処理開始なのだ: {filePath}");

            // キャッシュをチェック - 既に処理済みの場合はスキップ
            if (IsFileAlreadyProcessed(filePath))
            {
                _logMessage($"ファイルをスキップしたのだ: {filePath}");
                return;
            }

            // 単一ファイルのリストを作成
            var files = new[] { filePath };
            var folders = new string[] { };

            // JSON文字列をファイルに保存して、ファイルパスを渡す
            var tempDirectory = Path.GetTempPath();
            tempFilePathsJson = Path.Combine(tempDirectory, $"markitdown_files_{Guid.NewGuid():N}.json");
            tempFolderPathsJson = Path.Combine(tempDirectory, $"markitdown_folders_{Guid.NewGuid():N}.json");

            // BOMなしのUTF-8でファイルを保存
            var utf8NoBom = new UTF8Encoding(false);
            var filePathsJson = JsonSerializer.Serialize(files);
            var folderPathsJson = JsonSerializer.Serialize(folders);

            File.WriteAllText(tempFilePathsJson, filePathsJson, utf8NoBom);
            File.WriteAllText(tempFolderPathsJson, folderPathsJson, utf8NoBom);

            // Pythonスクリプトを実行
            await Task.Run(() => _markItDownProcessor.ExecuteMarkItDownConvertScript(appDir, tempFilePathsJson, tempFolderPathsJson));
            _logMessage($"ファイル処理完了なのだ: {filePath}");
        }
        catch (Exception ex)
        {
            _logError($"ファイル処理エラーなのだ ({filePath}): {ex.Message}");
        }
        finally
        {
            CleanupTempFile(tempFilePathsJson);
            CleanupTempFile(tempFolderPathsJson);
        }
    }

    /// <summary>
    /// Process a single folder using MarkItDown
    /// </summary>
    /// <param name="appDir">Application directory</param>
    /// <param name="folderPath">Folder path to process</param>
    private async Task ProcessSingleFolderAsync(string appDir, string folderPath)
    {
        string? tempFilePathsJson = null;
        string? tempFolderPathsJson = null;

        try
        {
            _logMessage($"フォルダ処理開始なのだ: {folderPath}");

            // 単一フォルダのリストを作成
            var files = new string[] { };
            var folders = new[] { folderPath };

            // JSON文字列をファイルに保存して、ファイルパスを渡す
            var tempDirectory = Path.GetTempPath();
            tempFilePathsJson = Path.Combine(tempDirectory, $"markitdown_files_{Guid.NewGuid():N}.json");
            tempFolderPathsJson = Path.Combine(tempDirectory, $"markitdown_folders_{Guid.NewGuid():N}.json");

            // BOMなしのUTF-8でファイルを保存
            var utf8NoBom = new UTF8Encoding(false);
            var filePathsJson = JsonSerializer.Serialize(files);
            var folderPathsJson = JsonSerializer.Serialize(folders);

            File.WriteAllText(tempFilePathsJson, filePathsJson, utf8NoBom);
            File.WriteAllText(tempFolderPathsJson, folderPathsJson, utf8NoBom);

            // Pythonスクリプトを実行
            await Task.Run(() => _markItDownProcessor.ExecuteMarkItDownConvertScript(appDir, tempFilePathsJson, tempFolderPathsJson));
            _logMessage($"フォルダ処理完了なのだ: {folderPath}");
        }
        catch (Exception ex)
        {
            _logError($"フォルダ処理エラーなのだ ({folderPath}): {ex.Message}");
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
                _logMessage($"一時ファイルを削除したのだ: {path}");
            }
        }
        catch (Exception ex)
        {
            _logError($"一時ファイル削除に失敗したのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if file has been processed before (optimized: use file modification time instead of hash)
    /// </summary>
    /// <param name="filePath">File path to check</param>
    /// <returns>True if file was cached and not modified</returns>
    private bool IsFileAlreadyProcessed(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        var currentModifiedTime = fileInfo.LastWriteTimeUtc;

        if (_fileCache.TryGetValue(filePath, out var cached))
        {
            // Check if file modification time is unchanged and was cached within 5 minutes
            // UTC時刻を使用してシステム時刻ズレに対応
            if (cached.ticks == currentModifiedTime.Ticks && (DateTime.UtcNow - cached.timestamp).TotalSeconds < 300)
            {
                _logMessage($"キャッシュ: {Path.GetFileName(filePath)} は既に処理済みなのだ。");
                return true;
            }
        }

        // Update cache with modification time (Ticks) instead of hash
        // UTCタイムスタンプを使用
        _fileCache[filePath] = (currentModifiedTime.Ticks, DateTime.UtcNow);
        return false;
    }
}
