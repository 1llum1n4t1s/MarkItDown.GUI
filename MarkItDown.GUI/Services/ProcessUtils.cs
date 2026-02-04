using System;
using System.Diagnostics;
using System.Text;

namespace MarkItDown.GUI.Services;

/// <summary>
/// Utility class for process execution with secure argument handling
/// </summary>
public static class ProcessUtils
{
    /// <summary>
    /// Create a ProcessStartInfo for Python execution
    /// </summary>
    /// <param name="pythonPath">Path to Python executable</param>
    /// <param name="arguments">Arguments to pass to Python</param>
    /// <returns>Configured ProcessStartInfo</returns>
    public static ProcessStartInfo CreatePythonProcessInfo(string pythonPath, string argument)
    {
        return new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { argument }
        };
    }

    /// <summary>
    /// Create a ProcessStartInfo for Python execution with multiple arguments
    /// </summary>
    public static ProcessStartInfo CreatePythonProcessInfo(string pythonPath, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    /// <summary>
    /// Check if command is available in PATH
    /// </summary>
    public static bool TryCheckCommandVersion(string command, int timeoutMs, Action<string> logMessage)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            var exited = process.WaitForExit(timeoutMs);
            return exited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logMessage?.Invoke($"Command check failed: {command} - {ex.Message}");
            return false;
        }
    }
}
