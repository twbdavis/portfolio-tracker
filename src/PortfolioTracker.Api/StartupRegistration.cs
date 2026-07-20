using Microsoft.Win32;

namespace PortfolioTracker.Api;

/// Per-user "start with Windows" via the HKCU Run key — the API runs inside
/// the shell process, so ProcessPath IS the shell exe. Enable() always
/// rewrites the value, healing a moved or rebuilt exe path.
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PortfolioTracker";

    private static string LaunchCommand => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, LaunchCommand);
    }

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
