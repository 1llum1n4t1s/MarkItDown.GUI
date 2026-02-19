using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// アプリ内完結の埋め込みPython環境を管理する
/// </summary>
public partial class PythonEnvironmentManager : IDisposable
{
    private static readonly Version MinEmbeddedPythonVersion = new(3, 10, 0);
    private readonly SemaphoreSlim _pythonDetectionSemaphore = new(1, 1);
    private bool _disposed;
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Action<string> _logWarning;
    private readonly Action<double>? _progressCallback;
    private string _pythonExecutablePath = string.Empty;
    private bool _pythonAvailable;

    private static readonly HttpClient HttpClientForVersion = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    [GeneratedRegex(@"href=""(?<version>\d+\.\d+\.\d+)/""")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="logMessage">ログ出力用デリゲート</param>
    /// <param name="progressCallback">進捗コールバック関数（オプション）</param>
    public PythonEnvironmentManager(Action<string> logMessage, Action<double>? progressCallback = null, Action<string>? logError = null, Action<string>? logWarning = null)
    {
        _logMessage = logMessage;
        _logError = logError ?? logMessage;
        _logWarning = logWarning ?? logMessage;
        _progressCallback = progressCallback;
    }

    /// <summary>
    /// Python が利用可能かどうか
    /// </summary>
    public bool IsPythonAvailable => _pythonAvailable;

    /// <summary>
    /// Python 実行ファイルのフルパス（利用不可時は空文字）
    /// </summary>
    public string PythonExecutablePath => _pythonExecutablePath;

    /// <summary>
    /// 埋め込みPython環境を非同期で初期化する
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _logMessage("Python環境初期化開始なのだ");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _logMessage("エンコーディングプロバイダー登録完了なのだ");
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
            _logMessage("環境変数設定完了なのだ");

            var found = await FindEmbeddedPythonAsync(ct);
            if (found && !string.IsNullOrEmpty(_pythonExecutablePath))
            {
                _pythonAvailable = true;
                _logMessage($"Python実行ファイルなのだ: {_pythonExecutablePath}");
                _logMessage("Python環境の初期化が完了したのだ");
            }
            else
            {
                _pythonAvailable = false;
                _logError("埋め込みPythonの準備に失敗したのだ。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logWarning("Python環境の初期化がキャンセルされたのだ");
            _pythonAvailable = false;
        }
        catch (HttpRequestException ex)
        {
            _logError($"Python環境の初期化中にネットワークエラーなのだ: {ex.Message}");
            _pythonAvailable = false;
        }
        catch (IOException ex)
        {
            _logError($"Python環境の初期化中にI/Oエラーなのだ: {ex.Message}");
            _pythonAvailable = false;
        }
        catch (Exception ex)
        {
            _logError($"Python環境の初期化に失敗したのだ: {ex.Message}");
            _logMessage($"スタックトレースなのだ: {ex.StackTrace}");
            _pythonAvailable = false;
        }
    }

    /// <summary>
    /// 埋め込みPythonを検出またはダウンロードして準備する
    /// </summary>
    private async Task<bool> FindEmbeddedPythonAsync(CancellationToken ct)
    {
        await _pythonDetectionSemaphore.WaitAsync(ct);
        try
        {
            var libDirectory = AppPathHelper.LibDirectory;
            _logMessage($"ライブラリディレクトリなのだ: {libDirectory}");
            var managedEmbeddedPath = Path.Combine(libDirectory, "python", "python-embed");
            _logMessage($"管理対象Pythonパスなのだ: {managedEmbeddedPath}");

            var targetVersion = await GetTargetEmbeddedPythonVersionAsync(ct);
            _logMessage($"対象バージョンなのだ: {(string.IsNullOrEmpty(targetVersion) ? "なし" : targetVersion)}");

            if (!string.IsNullOrEmpty(targetVersion))
            {
                var managedReady = await EnsureEmbeddedPythonAsync(managedEmbeddedPath, targetVersion, ct);
                if (managedReady)
                {
                    _pythonExecutablePath = Path.Combine(managedEmbeddedPath, "python.exe");
                    _logMessage($"管理対象の埋め込みPythonを使用するのだ: {_pythonExecutablePath}");
                    return true;
                }
            }

            _logMessage("管理対象Pythonは利用できないのだ。別の場所を検索するのだ...");
            var libPythonDir = Path.Combine(libDirectory, "python");
            if (Directory.Exists(libPythonDir))
            {
                var pythonDirs = Directory.GetDirectories(libPythonDir, "python*embed*");
                foreach (var dir in pythonDirs)
                {
                    var pythonExe = Path.Combine(dir, "python.exe");
                    if (File.Exists(pythonExe))
                    {
                        _pythonExecutablePath = pythonExe;
                        _logMessage($"lib/python内の埋め込みPythonを検出したのだ: {_pythonExecutablePath}");
                        return true;
                    }
                }
            }

            var directEmbeddedPath = Path.Combine(AppPathHelper.LibDirectory, "python-embed");
            var directExe = Path.Combine(directEmbeddedPath, "python.exe");
            if (File.Exists(directExe))
            {
                _pythonExecutablePath = directExe;
                _logMessage($"直接的な埋め込みPythonを検出したのだ: {_pythonExecutablePath}");
                return true;
            }

            _logMessage("組み込みPythonが見つからないため、ダウンロードを試行するのだ。");
            if (await DownloadEmbeddedPythonAsync(ct))
            {
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logMessage($"埋め込みPython検出中にネットワークエラーなのだ: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            _logMessage($"埋め込みPython検出中にI/Oエラーなのだ: {ex.Message}");
            return false;
        }
        catch (InvalidDataException ex)
        {
            _logMessage($"埋め込みPython検出中にデータエラーなのだ: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"埋め込みPython検出中に予期しないエラーなのだ: {ex.Message}");
            return false;
        }
        finally
        {
            _pythonDetectionSemaphore.Release();
        }
    }

    /// <summary>
    /// 指定パスに埋め込みPythonが用意されているか確認する
    /// </summary>
    private static bool IsEmbeddedPythonReady(string embeddedPythonPath)
    {
        if (!Directory.Exists(embeddedPythonPath))
        {
            return false;
        }

        var pythonExePath = Path.Combine(embeddedPythonPath, "python.exe");
        if (!File.Exists(pythonExePath))
        {
            return false;
        }

        return Directory.GetFiles(embeddedPythonPath, "python*.dll").Length > 0;
    }

    private static string GetEmbeddedPythonVersionFilePath(string embeddedPythonPath)
        => Path.Combine(embeddedPythonPath, "python-embed-version.txt");

    private static string? ReadEmbeddedPythonVersion(string versionFilePath)
    {
        if (!File.Exists(versionFilePath))
        {
            return null;
        }

        var content = File.ReadAllText(versionFilePath).Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }

    private static void WriteEmbeddedPythonVersion(string versionFilePath, string version)
        => File.WriteAllText(versionFilePath, version);

    /// <summary>
    /// 指定パスに指定バージョンの埋め込みPythonが用意されているか確保する
    /// </summary>
    private async Task<bool> EnsureEmbeddedPythonAsync(string embeddedPythonPath, string targetVersion, CancellationToken ct)
    {
        var versionFilePath = GetEmbeddedPythonVersionFilePath(embeddedPythonPath);
        var currentVersion = ReadEmbeddedPythonVersion(versionFilePath);

        if (!IsEmbeddedPythonReady(embeddedPythonPath))
        {
            return await DownloadEmbeddedPythonAsync(ct);
        }

        EnableSitePackages(embeddedPythonPath);

        if (!string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (await DownloadEmbeddedPythonAsync(ct))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logWarning($"埋め込みPython更新中にネットワークエラーなのだ: {ex.Message}。既存のPythonを使用するのだ。");
            }
            catch (IOException ex)
            {
                _logWarning($"埋め込みPython更新中にI/Oエラーなのだ: {ex.Message}。既存のPythonを使用するのだ。");
            }
        }

        return true;
    }

    /// <summary>
    /// インストール対象の埋め込みPythonバージョンを決定する
    /// </summary>
    private async Task<string?> GetTargetEmbeddedPythonVersionAsync(CancellationToken ct)
    {
        var savedVersion = AppSettings.GetPythonVersion();
        if (!string.IsNullOrEmpty(savedVersion))
        {
            if (Version.TryParse(savedVersion, out var configuredVersion) &&
                !IsSupportedEmbeddedPythonVersion(configuredVersion))
            {
                AppSettings.SetPythonVersion(null);
            }
            else
            {
                try
                {
                    if (await IsEmbeddedPythonDownloadAvailableAsync(savedVersion, ct))
                    {
                        return savedVersion;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logWarning($"保存済みバージョン {savedVersion} の確認に失敗したのだ: {ex.Message}");
                }
                AppSettings.SetPythonVersion(null);
            }
        }

        var latestVersion = await GetLatestStablePythonVersionAsync(ct);
        if (!string.IsNullOrEmpty(latestVersion))
        {
            AppSettings.SetPythonVersion(latestVersion);
        }

        return latestVersion;
    }

    /// <summary>
    /// 公式の embeddable Python をダウンロードして展開し、pip を有効化する
    /// </summary>
    private async Task<bool> DownloadEmbeddedPythonAsync(CancellationToken ct)
    {
        var basePythonDir = Path.Combine(AppPathHelper.LibDirectory, "python");
        var embeddedPythonPath = Path.Combine(basePythonDir, "python-embed");
        var embeddedPythonBackupPath = embeddedPythonPath + ".backup";

        try
        {
            _logMessage("埋め込みPythonのダウンロード準備を開始するのだ。");
            var targetVersion = await ResolveDownloadablePythonVersionAsync(ct);
            if (string.IsNullOrEmpty(targetVersion))
            {
                _logMessage("最新のPythonバージョン情報を取得できなかったのだ。");
                return false;
            }

            var versionFilePath = GetEmbeddedPythonVersionFilePath(embeddedPythonPath);
            var currentVersion = ReadEmbeddedPythonVersion(versionFilePath);

            if (IsEmbeddedPythonReady(embeddedPythonPath) &&
                string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                EnableSitePackages(embeddedPythonPath);
                _pythonExecutablePath = Path.Combine(embeddedPythonPath, "python.exe");
                _logMessage($"既に埋め込みPythonが存在するのだ: {_pythonExecutablePath}");
                return true;
            }

            var zipFileName = $"python-{targetVersion}-embed-amd64.zip";
            var zipPath = Path.Combine(basePythonDir, zipFileName);
            var downloadUrl = new Uri($"https://www.python.org/ftp/python/{targetVersion}/{zipFileName}");
            if (Directory.Exists(embeddedPythonPath))
            {
                if (Directory.Exists(embeddedPythonBackupPath))
                {
                    Directory.Delete(embeddedPythonBackupPath, true);
                }
                Directory.Move(embeddedPythonPath, embeddedPythonBackupPath);
            }

            try
            {
                Directory.CreateDirectory(embeddedPythonPath);
            }
            catch (IOException ex)
            {
                _logError($"ディレクトリ作成エラーなのだ: {ex.Message}");
                RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logError($"ディレクトリ作成権限エラーなのだ: {ex.Message}");
                RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
                return false;
            }

            if (!File.Exists(zipPath))
            {
                _logMessage($"埋め込みPythonをダウンロード中なのだ: {downloadUrl}");
                _progressCallback?.Invoke(0);

                using var response = await HttpClientForVersion.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                // ダウンロードサイズの上限チェック（1GB以上は拒否）
                const long MaxDownloadSize = 1024L * 1024L * 1024L; // 1GB

                if (totalBytes <= 0)
                {
                    _logWarning("警告: ダウンロードサイズが不明なのだ。続行するのだ。");
                }
                else if (totalBytes > MaxDownloadSize)
                {
                    _logError($"エラー: ダウンロードサイズが大きすぎるのだ（{totalBytes / 1024 / 1024 / 1024}GB > 1GB）");
                    throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                }
                else
                {
                    _logMessage($"ダウンロードサイズなのだ: {totalBytes / 1024 / 1024:F2} MB");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920]; // 80KB — ネットワークI/Oのシステムコール回数を削減
                var totalBytesRead = 0L;
                int bytesRead;
                var lastReportedProgress = 0.0;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    // ダウンロードサイズの追加チェック
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxDownloadSize)
                    {
                        _logError($"エラー: ダウンロードサイズが上限を超えたのだ");
                        throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

                    if (totalBytes > 0)
                    {
                        var progress = (double)totalBytesRead / totalBytes * 100;

                        if (progress - lastReportedProgress >= 0.5)
                        {
                            _progressCallback?.Invoke(progress);
                            lastReportedProgress = progress;
                        }

                        if (totalBytesRead / (1024 * 1024) > (totalBytesRead - bytesRead) / (1024 * 1024))
                        {
                            _logMessage($"ダウンロード進捗なのだ: {progress:F1}% ({totalBytesRead / 1024 / 1024:F2} MB / {totalBytes / 1024 / 1024:F2} MB)");
                        }
                    }
                }

                _progressCallback?.Invoke(100);
                _logMessage("埋め込みPythonのダウンロードが完了したのだ。");
            }

            _logMessage("埋め込みPythonを展開中なのだ...");
            _progressCallback?.Invoke(0);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, embeddedPythonPath, true);
                _progressCallback?.Invoke(100);
                _logMessage("埋め込みPythonの展開が完了したのだ。");
            }
            catch (InvalidDataException ex)
            {
                _logError($"展開エラー（zipファイルが破損している可能性）なのだ: {ex.Message}");
                RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
                return false;
            }
            catch (IOException ex)
            {
                _logError($"展開I/Oエラーなのだ: {ex.Message}");
                RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
                return false;
            }

            // 展開成功後、zipファイルを削除してディスク容量を節約
            try
            {
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                    _logMessage("ダウンロードしたzipファイルを削除したのだ。");
                }
            }
            catch (IOException ex)
            {
                _logWarning($"zipファイルの削除に失敗したのだ（処理は継続するのだ）: {ex.Message}");
            }

            EnableSitePackages(embeddedPythonPath);
            await BootstrapPipAsync(embeddedPythonPath, ct);

            if (!IsEmbeddedPythonReady(embeddedPythonPath))
            {
                _logError("埋め込みPythonの展開に失敗したのだ。");
                RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
                return false;
            }

            // 成功したのでバックアップを削除
            if (Directory.Exists(embeddedPythonBackupPath))
            {
                try
                {
                    Directory.Delete(embeddedPythonBackupPath, true);
                }
                catch (IOException)
                {
                    _logWarning("バックアップ削除エラーなのだ（処理は継続するのだ）");
                }
            }

            WriteEmbeddedPythonVersion(versionFilePath, targetVersion);
            AppSettings.SetPythonVersion(targetVersion);
            _pythonExecutablePath = Path.Combine(embeddedPythonPath, "python.exe");
            _logMessage($"埋め込みPythonの準備が完了したのだ: {_pythonExecutablePath}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logError($"埋め込みPythonのダウンロード/展開に失敗したのだ: {ex.Message}");
            RestoreFromBackup(embeddedPythonPath, embeddedPythonBackupPath);
            return false;
        }
    }

    /// <summary>
    /// バックアップからPython環境を復旧する
    /// </summary>
    private void RestoreFromBackup(string embeddedPythonPath, string backupPath)
    {
        try
        {
            if (Directory.Exists(backupPath))
            {
                if (Directory.Exists(embeddedPythonPath))
                {
                    Directory.Delete(embeddedPythonPath, true);
                }
                Directory.Move(backupPath, embeddedPythonPath);
                _logMessage("バックアップから復旧したのだ。");
            }
        }
        catch (IOException ex)
        {
            _logError($"復旧に失敗したのだ: {ex.Message}");
        }
    }

    /// <summary>
    /// 埋め込みPythonの ._pth で import site と site-packages パスを有効化する
    /// </summary>
    private static void EnableSitePackages(string embeddedPythonPath)
    {
        var pthFiles = Directory.GetFiles(embeddedPythonPath, "python*._pth");
        foreach (var pthPath in pthFiles)
        {
            var lines = File.ReadAllLines(pthPath);
            var modified = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.Equals("# import site", StringComparison.Ordinal) ||
                    trimmed.Equals("#import site", StringComparison.Ordinal))
                {
                    lines[i] = line.Replace("# ", "").Replace("#", "").TrimStart();
                    modified = true;
                }
            }

            var hasSitePackages = false;
            foreach (var l in lines)
            {
                if (l.Trim().Contains("site-packages", StringComparison.Ordinal))
                {
                    hasSitePackages = true;
                    break;
                }
            }

            if (!hasSitePackages)
            {
                var newLines = new List<string>();
                foreach (var line in lines)
                {
                    newLines.Add(line);
                    if (line.Trim().Equals(".", StringComparison.Ordinal))
                    {
                        newLines.Add("Lib\\site-packages");
                        modified = true;
                    }
                }

                if (modified)
                {
                    lines = newLines.ToArray();
                }
            }

            if (modified)
            {
                File.WriteAllLines(pthPath, lines);
            }
        }
    }

    /// <summary>
    /// get-pip.py をダウンロードして実行し、pip を導入する
    /// </summary>
    private async Task BootstrapPipAsync(string embeddedPythonPath, CancellationToken ct)
    {
        var pythonExe = Path.Combine(embeddedPythonPath, "python.exe");
        var getPipPath = Path.Combine(embeddedPythonPath, "get-pip.py");

        try
        {
            _logMessage("pip を導入するため get-pip.py をダウンロード中なのだ...");
            const string getPipUrl = "https://bootstrap.pypa.io/get-pip.py";
            using var response = await HttpClientForVersion.GetAsync(getPipUrl, ct);
            response.EnsureSuccessStatusCode();
            var script = await response.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(getPipPath, script, ct);
            _logMessage("get-pip.py のダウンロードが完了したのだ。");

            _logMessage("get-pip.py を実行中なのだ...");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = embeddedPythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("get-pip.py");

            var (exitCode, output, error) = await ProcessUtils.RunAsync(
                startInfo, TimeoutSettings.PackageInstallTimeoutMs, ct);

            if (!string.IsNullOrWhiteSpace(output))
            {
                _logMessage($"get-pip 出力: {output.Trim()}");
            }

            if (exitCode != 0)
            {
                _logMessage($"get-pip 終了コードなのだ: {exitCode}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logError($"get-pip エラー: {error.Trim()}");
                }
            }
            else
            {
                _logMessage("pip の導入が完了したのだ。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logError($"pip 導入中にネットワークエラーなのだ: {ex.Message}");
        }
        catch (IOException ex)
        {
            _logError($"pip 導入中にI/Oエラーなのだ: {ex.Message}");
        }
        finally
        {
            if (File.Exists(getPipPath))
            {
                try
                {
                    File.Delete(getPipPath);
                }
                catch (IOException)
                {
                    // ファイル削除失敗は無視（一時ファイル）
                }
            }
        }
    }

    private async Task<string?> GetLatestStablePythonVersionAsync(CancellationToken ct)
    {
        try
        {
            var indexContent = await HttpClientForVersion.GetStringAsync("https://www.python.org/ftp/python/", ct);
            var versions = new List<Version>();
            var matches = VersionRegex().Matches(indexContent);

            foreach (Match match in matches)
            {
                var versionText = match.Groups["version"].Value;
                if (Version.TryParse(versionText, out var version))
                {
                    versions.Add(version);
                }
            }

            if (versions.Count == 0)
            {
                return null;
            }

            versions.Sort((a, b) => b.CompareTo(a));

            foreach (var version in versions)
            {
                if (!IsSupportedEmbeddedPythonVersion(version))
                {
                    continue;
                }

                if (await IsEmbeddedPythonDownloadAvailableAsync(version.ToString(), ct))
                {
                    return version.ToString();
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logWarning($"最新Pythonバージョン取得に失敗したのだ: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> IsEmbeddedPythonDownloadAvailableAsync(string version, CancellationToken ct)
    {
        var zipFileName = $"python-{version}-embed-amd64.zip";
        var downloadUrl = new Uri($"https://www.python.org/ftp/python/{version}/{zipFileName}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            using var response = await HttpClientForVersion.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using var getResponse = await HttpClientForVersion.SendAsync(getRequest, cts.Token);
            return getResponse.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logWarning($"ダウンロード可否確認がタイムアウトなのだ ({version})");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logWarning($"ダウンロード可否確認エラーなのだ ({version}): {ex.Message}");
            return false;
        }
    }

    private async Task<string?> ResolveDownloadablePythonVersionAsync(CancellationToken ct)
    {
        var targetVersion = await GetTargetEmbeddedPythonVersionAsync(ct);
        if (!string.IsNullOrEmpty(targetVersion) &&
            Version.TryParse(targetVersion, out var parsed) &&
            IsSupportedEmbeddedPythonVersion(parsed) &&
            await IsEmbeddedPythonDownloadAvailableAsync(targetVersion, ct))
        {
            return targetVersion;
        }

        return await GetLatestStablePythonVersionAsync(ct);
    }

    /// <summary>
    /// サポートされている埋め込みPythonバージョンかどうかを判定する
    /// </summary>
    /// <param name="version">チェックするバージョン</param>
    /// <returns>サポートされている場合はtrue</returns>
    private static bool IsSupportedEmbeddedPythonVersion(Version version)
        => version >= MinEmbeddedPythonVersion;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pythonDetectionSemaphore.Dispose();
    }
}
