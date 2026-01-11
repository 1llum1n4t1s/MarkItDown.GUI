using System;
using System.IO;
using System.Xml.Linq;

namespace MarkItDownX.Services;

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
    /// Get the default update feed URL (GitHub releases)
    /// </summary>
    private static string GetDefaultUpdateFeedUrl()
    {
        return "https://github.com/1llum1n4t1s/MarkItDownX/releases/latest/download";
    }

    /// <summary>
    /// Create default settings file
    /// </summary>
    private static void CreateDefaultSettings()
    {
        try
        {
            var root = new XElement("AppSettings",
                new XElement("UpdateFeedUrl", GetDefaultUpdateFeedUrl())
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
