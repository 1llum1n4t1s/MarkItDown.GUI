using System;
using System.Windows;
using MarkItDownX.Services;
using Velopack;

namespace MarkItDownX;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Load application settings from configuration file
        AppSettings.LoadSettings();
        var updateFeedUrl = AppSettings.GetUpdateFeedUrl();

        try
        {
            var updateManager = new UpdateManager(updateFeedUrl);
            var updateInfo = await updateManager.CheckForUpdatesAsync();
            if (updateInfo is not null)
            {
                await updateManager.DownloadUpdatesAsync(updateInfo);
                updateManager.ApplyUpdatesAndRestart(updateInfo);
                return;
            }
        }
        catch (Exception)
        {
            // Update check failed, skipping...
        }

        base.OnStartup(e);
    }
}
