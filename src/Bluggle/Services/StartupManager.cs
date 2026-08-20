using Microsoft.Win32;

namespace Bluggle.Services;

/// <summary>
/// "Start with Windows", implemented as a value under the per-user Run key. HKCU needs no
/// elevation, unlike HKLM or a scheduled task, so this works from a normal user account.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = ConfigStore.AppName;

    /// <summary>
    /// Path of the running executable. Assembly.Location is empty under single-file publish,
    /// so Environment.ProcessPath is the only thing that works for both build shapes.
    /// </summary>
    public static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns null on success, or a short message to show in the widget tooltip.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Run key unavailable.");

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            string? exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe))
                return "Could not determine the executable path.";

            // Quoted: the path very often contains spaces, and an unquoted Run value with a
            // space is parsed as command + arguments by the shell.
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not update startup entry: {ex.Message}";
        }
    }

    /// <summary>
    /// Rewrites the Run value if the exe has moved since it was written. Cheap insurance
    /// against a stale entry silently doing nothing after the user relocates the app.
    /// </summary>
    public static void RefreshPathIfStale()
    {
        try
        {
            if (!IsEnabled()) return;

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current) return;

            string? exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return;

            string expected = $"\"{exe}\"";
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
        catch
        {
            // Non-critical.
        }
    }
}
