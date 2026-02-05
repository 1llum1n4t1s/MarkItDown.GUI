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
        AppDomain.CurrentDomain.BaseDirectory,
        "appsettings.xml");

    private static XDocument? _settingsDocument;

    /// <summary>
    /// Load application settings from configuration file
    /// </summary>
    public static void LoadSettings()
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

    /// <summary>
    /// Get update feed URL from configuration
    /// </summary>
    public static string GetUpdateFeedUrl()
    {
        try
        {
            var url = _settingsDocument?.Root?.Element("UpdateFeedUrl")?.Value;
            return !string.IsNullOrEmpty(url)
                ? url
                : GetDefaultUpdateFeedUrl();
        }
        catch
        {
            return GetDefaultUpdateFeedUrl();
        }
    }

    /// <summary>
    /// 埋め込みPythonのバージョン文字列を取得する
    /// </summary>
    public static string? GetPythonVersion()
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

    /// <summary>
    /// 埋め込みPythonのバージョン文字列を保存する
    /// </summary>
    /// <param name="version">バージョン文字列（例: 3.12.0）</param>
    public static void SetPythonVersion(string? version)
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

    /// <summary>
    /// OllamaのエンドポイントURLを取得する
    /// </summary>
    public static string? GetOllamaUrl()
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

    /// <summary>
    /// OllamaのエンドポイントURLを保存する
    /// </summary>
    /// <param name="url">OllamaのURL（例: http://localhost:11434）</param>
    public static void SetOllamaUrl(string? url)
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

    /// <summary>
    /// Ollamaで使用するモデル名を取得する
    /// </summary>
    public static string? GetOllamaModel()
    {
        try
        {
            var model = _settingsDocument?.Root?.Element("OllamaModel")?.Value;
            return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ollamaで使用するモデル名を保存する
    /// </summary>
    /// <param name="model">モデル名（例: llava）</param>
    public static void SetOllamaModel(string? model)
    {
        try
        {
            var root = _settingsDocument?.Root;
            if (root is null)
            {
                return;
            }

            var element = root.Element("OllamaModel");
            if (string.IsNullOrEmpty(model))
            {
                element?.Remove();
            }
            else
            {
                if (element is null)
                {
                    root.Add(new XElement("OllamaModel", model));
                }
                else
                {
                    element.Value = model;
                }
            }

            _settingsDocument?.Save(SettingsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save OllamaModel: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the default update feed URL (GitHub releases)
    /// </summary>
    private static string GetDefaultUpdateFeedUrl()
    {
        return "https://github.com/1llum1n4t1s/MarkItDown.GUI/releases/latest/download";
    }

    /// <summary>
    /// Create default settings file
    /// </summary>
    private static void CreateDefaultSettings()
    {
        try
        {
            var root = new XElement("AppSettings",
                new XElement("UpdateFeedUrl", GetDefaultUpdateFeedUrl()),
                new XElement("PythonVersion", ""),
                new XElement("OllamaUrl", "http://localhost:11434"),
                new XElement("OllamaModel", "llava:34b")
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
