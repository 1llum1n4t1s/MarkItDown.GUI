using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        AppPathHelper.SettingsDirectory,
        "appsettings.xml");

    private static readonly object _lock = new();
    private static XDocument? _settingsDocument;

    /// <summary>
    /// Load application settings from configuration file
    /// </summary>
    public static void LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    _settingsDocument = XDocument.Load(SettingsPath);
                }
                else
                {
                    // Create default settings file
                    CreateDefaultSettings();
                }
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
                CreateDefaultSettings();
            }
            catch (XmlException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse settings XML: {ex.Message}");
                CreateDefaultSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings (access denied): {ex.Message}");
                CreateDefaultSettings();
            }
        }
    }

    /// <summary>
    /// 自動更新用のGitHubオーナー名を取得する
    /// </summary>
    public static string GetUpdateRepoOwner()
    {
        lock (_lock)
        {
            try
            {
                var value = _settingsDocument?.Root?.Element("UpdateRepoOwner")?.Value;
                return !string.IsNullOrEmpty(value) ? value : "1llum1n4t1s";
            }
            catch (InvalidOperationException)
            {
                return "1llum1n4t1s";
            }
            catch (XmlException)
            {
                return "1llum1n4t1s";
            }
        }
    }

    /// <summary>
    /// 自動更新用のGitHubリポジトリ名を取得する
    /// </summary>
    public static string GetUpdateRepoName()
    {
        lock (_lock)
        {
            try
            {
                var value = _settingsDocument?.Root?.Element("UpdateRepoName")?.Value;
                return !string.IsNullOrEmpty(value) ? value : "MarkItDown.GUI";
            }
            catch (InvalidOperationException)
            {
                return "MarkItDown.GUI";
            }
            catch (XmlException)
            {
                return "MarkItDown.GUI";
            }
        }
    }

    /// <summary>
    /// 自動更新用のチャンネル名を取得する
    /// </summary>
    public static string GetUpdateChannel()
    {
        lock (_lock)
        {
            try
            {
                var value = _settingsDocument?.Root?.Element("UpdateChannel")?.Value;
                return !string.IsNullOrEmpty(value) ? value : "release";
            }
            catch (InvalidOperationException)
            {
                return "release";
            }
            catch (XmlException)
            {
                return "release";
            }
        }
    }

    /// <summary>
    /// 埋め込みPythonのバージョン文字列を取得する
    /// </summary>
    public static string? GetPythonVersion()
    {
        lock (_lock)
        {
            try
            {
                var version = _settingsDocument?.Root?.Element("PythonVersion")?.Value;
                return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 埋め込みPythonのバージョン文字列を保存する
    /// </summary>
    /// <param name="version">バージョン文字列（例: 3.12.0）</param>
    public static void SetPythonVersion(string? version)
    {
        lock (_lock)
        {
            try
            {
                var root = _settingsDocument?.Root;
                if (root is null)
                {
                    return;
                }

                var element = root.Element("PythonVersion");
                if (string.IsNullOrEmpty(version))
                {
                    element?.Remove();
                }
                else
                {
                    if (element is null)
                    {
                        root.Add(new XElement("PythonVersion", version));
                    }
                    else
                    {
                        element.Value = version;
                    }
                }

                EnsureSettingsDirectory();
                _settingsDocument?.Save(SettingsPath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save PythonVersion: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save PythonVersion: {ex.Message}");
            }
            catch (XmlException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save PythonVersion: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Claude AI を使用するかどうかを取得する
    /// </summary>
    public static bool GetUseClaudeAI()
    {
        lock (_lock)
        {
            try
            {
                var value = _settingsDocument?.Root?.Element("UseClaudeAI")?.Value;
                return bool.TryParse(value, out var result) && result;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Claude AI を使用するかどうかを保存する
    /// </summary>
    /// <param name="useClaudeAI">Claude AI を使用するかどうか</param>
    public static void SetUseClaudeAI(bool useClaudeAI)
    {
        lock (_lock)
        {
            try
            {
                var root = _settingsDocument?.Root;
                if (root is null)
                {
                    return;
                }

                var element = root.Element("UseClaudeAI");
                if (element is null)
                {
                    root.Add(new XElement("UseClaudeAI", useClaudeAI.ToString().ToLowerInvariant()));
                }
                else
                {
                    element.Value = useClaudeAI.ToString().ToLowerInvariant();
                }

                EnsureSettingsDirectory();
                _settingsDocument?.Save(SettingsPath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UseClaudeAI: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UseClaudeAI: {ex.Message}");
            }
            catch (XmlException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UseClaudeAI: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Create default settings file
    /// </summary>
    private static void CreateDefaultSettings()
    {
        try
        {
            var root = new XElement("AppSettings",
                new XElement("UpdateRepoOwner", "1llum1n4t1s"),
                new XElement("UpdateRepoName", "MarkItDown.GUI"),
                new XElement("UpdateChannel", "release"),
                new XElement("PythonVersion", ""),
                new XElement("UseClaudeAI", "false")
            );

            _settingsDocument = new XDocument(root);
            EnsureSettingsDirectory();
            _settingsDocument.Save(SettingsPath);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create default settings: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create default settings: {ex.Message}");
        }
        catch (XmlException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create default settings: {ex.Message}");
        }
    }

    /// <summary>
    /// 設定ファイルのディレクトリが存在することを保証する
    /// </summary>
    private static void EnsureSettingsDirectory()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
