using Microsoft.Win32;

namespace VpnWatchdog.Gui;

/// <summary>
/// Registers or unregisters this exe to launch when the current user logs into
/// Windows, via the per-user Registry Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Deliberately the
/// per-user key, not the machine-wide <c>HKLM</c> equivalent or a Scheduled Task:
/// it needs no elevation, matching the rest of this app, which never asks for
/// admin rights.
/// <para>
/// Every method here is a best-effort convenience, never a dependency: a failed
/// (un)registration (a policy-restricted profile, unusual ACLs, a read-only
/// hive) must never crash the settings dialog or stop the app from starting.
/// </para>
/// </summary>
internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VpnWatchdog";

    /// <summary>
    /// Writes the Run key so this exe launches at the next login. Re-resolves the
    /// CURRENT process path every call (via <see cref="Environment.ProcessPath"/>,
    /// which - unlike <c>Assembly.Location</c> - reports the real exe path even
    /// for a single-file-published build), so calling this again after the exe
    /// was moved or reinstalled elsewhere self-heals a stale registration rather
    /// than leaving Windows trying to launch a path that no longer exists.
    /// </summary>
    public static void Enable()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        catch
        {
            // Losing auto-start-with-Windows is a nuisance, never worth a crash.
        }
    }

    /// <summary>Removes the Run key entry, if any. Safe to call whether or not one exists.</summary>
    public static void Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Same reasoning as Enable - never worth a crash.
        }
    }
}
