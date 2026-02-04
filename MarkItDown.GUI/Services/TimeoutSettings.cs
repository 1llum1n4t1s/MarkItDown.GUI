namespace MarkItDown.GUI.Services;

/// <summary>
/// Centralized timeout configuration for all process operations
/// </summary>
public static class TimeoutSettings
{
    /// <summary>
    /// Timeout for Python version check (5 seconds)
    /// </summary>
    public const int PythonVersionCheckTimeoutMs = 5000;

    /// <summary>
    /// Timeout for markitdown availability check (30 seconds)
    /// </summary>
    public const int MarkItDownCheckTimeoutMs = 30000;

    /// <summary>
    /// Timeout for markitdown package installation (60 seconds)
    /// </summary>
    public const int PackageInstallTimeoutMs = 60000;

    /// <summary>
    /// Timeout for FFmpeg check (5 seconds)
    /// </summary>
    public const int FFmpegCheckTimeoutMs = 5000;

    /// <summary>
    /// Timeout for FFmpeg installation (2 minutes)
    /// </summary>
    public const int FFmpegInstallTimeoutMs = 120000;

    /// <summary>
    /// Timeout for package uninstall (30 seconds)
    /// </summary>
    public const int PackageUninstallTimeoutMs = 30000;

    /// <summary>
    /// Timeout for generic command execution (default)
    /// </summary>
    public const int DefaultCommandTimeoutMs = 30000;
}
