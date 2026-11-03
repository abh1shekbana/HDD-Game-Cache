using System;
using System.Diagnostics;
using System.Security.Principal;

namespace SmartGameCache.Core.Monitoring;

public static class ElevationHelper
{
    public static bool IsAdministrator()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool RestartElevated(string? arguments = null)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas" // Triggers standard Windows UAC prompt
            };

            Process.Start(startInfo);
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restart elevated: {ex.Message}");
            return false;
        }
    }
}

