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
public partial class PythonEnvironmentManager
{
    private static readonly Version MinEmbeddedPythonVersion = new(3, 10, 0);
    private readonly SemaphoreSlim _pythonDetectionSemaphore = new(1, 1);
    private readonly Action<string> _logMessage;
    private readonly Action<double>? _progressCallback;
    private string _pythonExecutablePath = string.Empty;
    private bool _pythonAvailable;

    private static readonly HttpClient HttpClientForVersion = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    [GeneratedRegex(@"href=""(?<version>\d+\.\d+\.\d+)/""")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// コンストラクタ
    /// </summary>
    /// <param name="logMessage">ログ出力用デリゲート</param>
    /// <param name="progressCallback">進捗コールバック関数（オプション）</param>
    public PythonEnvironmentManager(Action<string> logMessage, Action<double>? progressCallback = null)
    {
        _logMessage = logMessage;
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
    public async Task InitializeAsync()
    {
        try
        {
            _logMessage("Python環境初期化開始");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _logMessage("エンコーディングプロバイダー登録完了");
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
            _logMessage("環境変数設定完了");

            var found = await FindEmbeddedPythonAsync();
            if (found && !string.IsNullOrEmpty(_pythonExecutablePath))
            {
                _pythonAvailable = true;
                _logMessage($"Python実行ファイル: {_pythonExecutablePath}");
                _logMessage("Python環境の初期化が完了しました");
            }
            else
            {
                _pythonAvailable = false;
                _logMessage("埋め込みPythonの準備に失敗しました。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"Python環境の初期化に失敗しました: {ex.Message}");
            _logMessage($"スタックトレース: {ex.StackTrace}");
            _pythonAvailable = false;
        }
    }

    /// <summary>
    /// 埋め込みPythonを検出またはダウンロードして準備する
    /// </summary>
    private async Task<bool> FindEmbeddedPythonAsync()
    {
        await _pythonDetectionSemaphore.WaitAsync();
        try
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _logMessage($"アプリケーションディレクトリ: {appDirectory}");
            var managedEmbeddedPath = Path.Combine(appDirectory, "lib", "python", "python-embed");
            _logMessage($"管理対象Pythonパス: {managedEmbeddedPath}");

            var targetVersion = await GetTargetEmbeddedPythonVersionAsync();
            _logMessage($"対象バージョン: {(string.IsNullOrEmpty(targetVersion) ? "なし" : targetVersion)}");

            if (!string.IsNullOrEmpty(targetVersion))
            {
                var managedReady = await EnsureEmbeddedPythonAsync(managedEmbeddedPath, targetVersion);
                if (managedReady)
                {
                    _pythonExecutablePath = Path.Combine(managedEmbeddedPath, "python.exe");
                    _logMessage($"管理対象の埋め込みPythonを使用します: {_pythonExecutablePath}");
                    return true;
                }
            }

            _logMessage("管理対象Pythonは利用できません。別の場所を検索します...");
            var libPythonDir = Path.Combine(appDirectory, "lib", "python");
            if (Directory.Exists(libPythonDir))
            {
                var pythonDirs = Directory.GetDirectories(libPythonDir, "python*embed*");
                foreach (var dir in pythonDirs)
                {
                    var pythonExe = Path.Combine(dir, "python.exe");
                    if (File.Exists(pythonExe))
                    {
                        _pythonExecutablePath = pythonExe;
                        _logMessage($"lib/python内の埋め込みPythonを検出しました: {_pythonExecutablePath}");
                        return true;
                    }
                }
            }

            var directEmbeddedPath = Path.Combine(appDirectory, "python-embed");
            var directExe = Path.Combine(directEmbeddedPath, "python.exe");
            if (File.Exists(directExe))
            {
                _pythonExecutablePath = directExe;
                _logMessage($"直接的な埋め込みPythonを検出しました: {_pythonExecutablePath}");
                return true;
            }

            _logMessage("組み込みPythonが見つからないため、ダウンロードを試行します。");
            if (await DownloadEmbeddedPythonAsync())
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logMessage($"埋め込みPython検出中に例外: {ex.Message}");
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
    private async Task<bool> EnsureEmbeddedPythonAsync(string embeddedPythonPath, string targetVersion)
    {
        var versionFilePath = GetEmbeddedPythonVersionFilePath(embeddedPythonPath);
        var currentVersion = ReadEmbeddedPythonVersion(versionFilePath);

        if (!IsEmbeddedPythonReady(embeddedPythonPath))
        {
            return await DownloadEmbeddedPythonAsync();
        }

        EnableSitePackages(embeddedPythonPath);

        if (!string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (await DownloadEmbeddedPythonAsync())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logMessage($"埋め込みPython更新中にエラー: {ex.Message}。既存のPythonを使用します。");
            }
        }

        return true;
    }

    /// <summary>
    /// インストール対象の埋め込みPythonバージョンを決定する
    /// </summary>
    private async Task<string?> GetTargetEmbeddedPythonVersionAsync()
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
                    if (await IsEmbeddedPythonDownloadAvailableAsync(savedVersion))
                    {
                        return savedVersion;
                    }
                }
                catch (Exception ex)
                {
                    _logMessage($"保存済みバージョン {savedVersion} の確認に失敗: {ex.Message}");
                }
                AppSettings.SetPythonVersion(null);
            }
        }

        var latestVersion = await GetLatestStablePythonVersionAsync();
        if (!string.IsNullOrEmpty(latestVersion))
        {
            AppSettings.SetPythonVersion(latestVersion);
        }

        return latestVersion;
    }

    /// <summary>
    /// 公式の embeddable Python をダウンロードして展開し、pip を有効化する
    /// </summary>
    private async Task<bool> DownloadEmbeddedPythonAsync()
    {
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var basePythonDir = Path.Combine(appDirectory, "lib", "python");
        var embeddedPythonPath = Path.Combine(basePythonDir, "python-embed");
        var embeddedPythonBackupPath = embeddedPythonPath + ".backup";

        try
        {
            _logMessage("埋め込みPythonのダウンロード準備を開始します。");
            var targetVersion = await ResolveDownloadablePythonVersionAsync();
            if (string.IsNullOrEmpty(targetVersion))
            {
                _logMessage("最新のPythonバージョン情報を取得できませんでした。");
                return false;
            }

            var versionFilePath = GetEmbeddedPythonVersionFilePath(embeddedPythonPath);
            var currentVersion = ReadEmbeddedPythonVersion(versionFilePath);

            if (IsEmbeddedPythonReady(embeddedPythonPath) &&
                string.Equals(currentVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                EnableSitePackages(embeddedPythonPath);
                _pythonExecutablePath = Path.Combine(embeddedPythonPath, "python.exe");
                _logMessage($"既に埋め込みPythonが存在します: {_pythonExecutablePath}");
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
            catch (Exception ex)
            {
                _logMessage($"ディレクトリ作成エラー: {ex.Message}");
                // バックアップから復旧
                if (Directory.Exists(embeddedPythonBackupPath))
                {
                    if (Directory.Exists(embeddedPythonPath))
                    {
                        Directory.Delete(embeddedPythonPath, true);
                    }
                    Directory.Move(embeddedPythonBackupPath, embeddedPythonPath);
                }
                return false;
            }

            if (!File.Exists(zipPath))
            {
                _logMessage($"埋め込みPythonをダウンロード中: {downloadUrl}");
                _progressCallback?.Invoke(0);

                using var response = await HttpClientForVersion.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;

                // ダウンロードサイズの上限チェック（1GB以上は拒否）
                const long MaxDownloadSize = 1024L * 1024L * 1024L; // 1GB

                if (totalBytes <= 0)
                {
                    _logMessage("警告: ダウンロードサイズが不明です。続行します。");
                }
                else if (totalBytes > MaxDownloadSize)
                {
                    _logMessage($"エラー: ダウンロードサイズが大きすぎます（{totalBytes / 1024 / 1024 / 1024}GB > 1GB）");
                    throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                }
                else
                {
                    _logMessage($"ダウンロードサイズ: {totalBytes / 1024 / 1024:F2} MB");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                var totalBytesRead = 0L;
                int bytesRead;
                var lastReportedProgress = 0.0;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // ダウンロードサイズの追加チェック
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxDownloadSize)
                    {
                        _logMessage($"エラー: ダウンロードサイズが上限を超えました");
                        throw new InvalidOperationException("ダウンロードサイズが上限を超えています");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    
                    if (totalBytes > 0)
                    {
                        var progress = (double)totalBytesRead / totalBytes * 100;
                        
                        if (progress - lastReportedProgress >= 0.5 || bytesRead < buffer.Length)
                        {
                            _progressCallback?.Invoke(progress);
                            lastReportedProgress = progress;
                        }
                        
                        if (totalBytesRead % (1024 * 1024) == 0 || bytesRead < buffer.Length)
                        {
                            _logMessage($"ダウンロード進捗: {progress:F1}% ({totalBytesRead / 1024 / 1024:F2} MB / {totalBytes / 1024 / 1024:F2} MB)");
                        }
                    }
                }
                
                _progressCallback?.Invoke(100);
            }

            _logMessage("埋め込みPythonを展開中...");
            _progressCallback?.Invoke(0);
            await Task.Delay(500);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, embeddedPythonPath, true);
                _progressCallback?.Invoke(100);
            }
            catch (Exception ex)
            {
                _logMessage($"展開エラー: {ex.Message}");
                // バックアップから復旧
                if (Directory.Exists(embeddedPythonBackupPath))
                {
                    if (Directory.Exists(embeddedPythonPath))
                    {
                        Directory.Delete(embeddedPythonPath, true);
                    }
                    Directory.Move(embeddedPythonBackupPath, embeddedPythonPath);
                    _logMessage("バックアップから復旧しました。");
                }
                return false;
            }

            EnableSitePackages(embeddedPythonPath);
            await BootstrapPipAsync(embeddedPythonPath);

            if (!IsEmbeddedPythonReady(embeddedPythonPath))
            {
                _logMessage("埋め込みPythonの展開に失敗しました。");
                // バックアップから復旧
                if (Directory.Exists(embeddedPythonBackupPath))
                {
                    if (Directory.Exists(embeddedPythonPath))
                    {
                        Directory.Delete(embeddedPythonPath, true);
                    }
                    Directory.Move(embeddedPythonBackupPath, embeddedPythonPath);
                    _logMessage("バックアップから復旧しました。");
                }
                return false;
            }

            // 成功したのでバックアップを削除
            if (Directory.Exists(embeddedPythonBackupPath))
            {
                try
                {
                    Directory.Delete(embeddedPythonBackupPath, true);
                }
                catch
                {
                    _logMessage("バックアップ削除エラー（処理は継続）");
                }
            }

            WriteEmbeddedPythonVersion(versionFilePath, targetVersion);
            AppSettings.SetPythonVersion(targetVersion);
            _pythonExecutablePath = Path.Combine(embeddedPythonPath, "python.exe");
            _logMessage($"埋め込みPythonの準備が完了しました: {_pythonExecutablePath}");
            return true;
        }
        catch (Exception ex)
        {
            _logMessage($"埋め込みPythonのダウンロード/展開に失敗しました: {ex.Message}");
            // バックアップから復旧（内側のtryで処理されなかった場合）
            try
            {
                if (Directory.Exists(embeddedPythonBackupPath))
                {
                    if (Directory.Exists(embeddedPythonPath))
                    {
                        Directory.Delete(embeddedPythonPath, true);
                    }
                    Directory.Move(embeddedPythonBackupPath, embeddedPythonPath);
                    _logMessage("バックアップから復旧しました。");
                }
            }
            catch
            {
                _logMessage("復旧に失敗しました。");
            }
            return false;
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
    private async Task BootstrapPipAsync(string embeddedPythonPath)
    {
        var pythonExe = Path.Combine(embeddedPythonPath, "python.exe");
        var getPipPath = Path.Combine(embeddedPythonPath, "get-pip.py");

        try
        {
            _logMessage("pip を導入するため get-pip.py をダウンロード中...");
            const string getPipUrl = "https://bootstrap.pypa.io/get-pip.py";
            using var response = await HttpClientForVersion.GetAsync(getPipUrl);
            response.EnsureSuccessStatusCode();
            var script = await response.Content.ReadAsStringAsync();
            await File.WriteAllTextAsync(getPipPath, script);

            _logMessage("get-pip.py を実行中...");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "get-pip.py",
                WorkingDirectory = embeddedPythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                _logMessage("get-pip.py の起動に失敗しました。");
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(cts.Token);
            if (!string.IsNullOrWhiteSpace(output))
            {
                _logMessage($"get-pip 出力: {output.Trim()}");
            }

            if (process.ExitCode != 0)
            {
                _logMessage($"get-pip 終了コード: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logMessage($"get-pip エラー: {error.Trim()}");
                }
            }
            else
            {
                _logMessage("pip の導入が完了しました。");
            }
        }
        catch (Exception ex)
        {
            _logMessage($"pip 導入中にエラー: {ex.Message}");
        }
        finally
        {
            if (File.Exists(getPipPath))
            {
                try
                {
                    File.Delete(getPipPath);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task<string?> GetLatestStablePythonVersionAsync()
    {
        try
        {
            var indexContent = await HttpClientForVersion.GetStringAsync("https://www.python.org/ftp/python/");
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

                if (await IsEmbeddedPythonDownloadAvailableAsync(version.ToString()))
                {
                    return version.ToString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logMessage($"最新Pythonバージョン取得に失敗: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> IsEmbeddedPythonDownloadAvailableAsync(string version)
    {
        var zipFileName = $"python-{version}-embed-amd64.zip";
        var downloadUrl = new Uri($"https://www.python.org/ftp/python/{version}/{zipFileName}");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
        catch (Exception ex)
        {
            _logMessage($"ダウンロード可否確認エラー ({version}): {ex.Message}");
            return false;
        }
    }

    private async Task<string?> ResolveDownloadablePythonVersionAsync()
    {
        var targetVersion = await GetTargetEmbeddedPythonVersionAsync();
        if (!string.IsNullOrEmpty(targetVersion) &&
            Version.TryParse(targetVersion, out var parsed) &&
            IsSupportedEmbeddedPythonVersion(parsed) &&
            await IsEmbeddedPythonDownloadAvailableAsync(targetVersion))
        {
            return targetVersion;
        }

        return await GetLatestStablePythonVersionAsync();
    }

    /// <summary>
    /// サポートされている埋め込みPythonバージョンかどうかを判定する
    /// </summary>
    /// <param name="version">チェックするバージョン</param>
    /// <returns>サポートされている場合はtrue</returns>
    private static bool IsSupportedEmbeddedPythonVersion(Version version)
        => version >= MinEmbeddedPythonVersion;
}
