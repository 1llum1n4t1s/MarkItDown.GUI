using System;
using System.IO;
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
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
            catch
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
            catch
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
            catch
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
            catch
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

                _settingsDocument?.Save(SettingsPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save PythonVersion: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// OllamaのエンドポイントURLを取得する
    /// </summary>
    public static string? GetOllamaUrl()
    {
        lock (_lock)
        {
            try
            {
                var url = _settingsDocument?.Root?.Element("OllamaUrl")?.Value;
                return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// OllamaのエンドポイントURLを保存する
    /// </summary>
    /// <param name="url">OllamaのURL（例: http://localhost:11434）</param>
    public static void SetOllamaUrl(string? url)
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

                var element = root.Element("OllamaUrl");
                if (string.IsNullOrEmpty(url))
                {
                    element?.Remove();
                }
                else
                {
                    if (element is null)
                    {
                        root.Add(new XElement("OllamaUrl", url));
                    }
                    else
                    {
                        element.Value = url;
                    }
                }

                _settingsDocument?.Save(SettingsPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save OllamaUrl: {ex.Message}");
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
                new XElement("OllamaUrl", "http://localhost:11434")
            );

            _settingsDocument = new XDocument(root);
            _settingsDocument.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create default settings: {ex.Message}");
        }
    }
}
