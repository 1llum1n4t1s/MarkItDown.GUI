using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkItDown.GUI.Services;

/// <summary>
/// SNSログインセッションの保存場所とアクセス権を管理する。
/// </summary>
public static class SocialSessionStorage
{
    /// <summary>
    /// 永続セッション用ディレクトリを作成し、Windows では現在ユーザーだけが読めるACLへ寄せる。
    /// </summary>
    public static async Task PreparePersistentDirectoryAsync(string directory, Action<string> logWarning, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            logWarning("SNSセッション保存先のユーザーSIDを取得できなかったのだ。");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("/inheritance:r");
        startInfo.ArgumentList.Add("/grant:r");
        startInfo.ArgumentList.Add($"*{userSid}:(OI)(CI)F");
        startInfo.ArgumentList.Add("/grant:r");
        startInfo.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");
        startInfo.ArgumentList.Add("/grant:r");
        startInfo.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");

        var (exitCode, _, error) = await ProcessUtils.RunAsync(startInfo, 10_000, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            logWarning($"SNSセッション保存先のACL設定に失敗したのだ: {error.Trim()}");
        }
    }

    /// <summary>
    /// 永続保存が無効な場合に、過去バージョンが残した既知のSNSセッション保存先を削除する。
    /// </summary>
    public static void DeleteKnownPersistentSessions(Action<string> logMessage, Action<string> logWarning)
    {
        var root = Path.GetFullPath(Path.Combine(AppPathHelper.LibDirectory, "playwright"));
        if (!Directory.Exists(root))
        {
            return;
        }

        var targets = new[]
        {
            Path.Combine(root, "x_session.json"),
            Path.Combine(root, "x_profile"),
            Path.Combine(root, "x_session"),
            Path.Combine(root, "instagram_session")
        };

        var deleted = 0;
        foreach (var target in targets)
        {
            var fullPath = Path.GetFullPath(target);
            if (!IsUnderRoot(root, fullPath))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                    deleted++;
                }
            }
            catch (IOException ex)
            {
                logWarning($"SNSセッション削除中にI/Oエラーなのだ: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                logWarning($"SNSセッション削除の権限がないのだ: {ex.Message}");
            }
        }

        if (deleted > 0)
        {
            logMessage("永続保存が無効なため、既存のSNSセッション保存データを削除したのだ。");
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
