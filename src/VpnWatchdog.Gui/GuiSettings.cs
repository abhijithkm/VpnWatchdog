using System.Text.Json;
using VpnWatchdog.Core;

namespace VpnWatchdog.Gui;

/// <summary>
/// The GUI's persisted preferences: the handful of <see cref="WatchdogConfig"/> values
/// a user can sensibly change from a dialog, plus two window-behaviour switches. Kept
/// as JSON at <c>%LOCALAPPDATA%\VpnWatchdog\gui-settings.json</c> - user-scoped and
/// writable without elevation, in the same folder as the shared activity log.
/// <para>
/// <see cref="Load"/> and <see cref="Save"/> never throw. A missing, corrupt or
/// unreadable file yields the built-in defaults and a failed save reports false: a
/// settings problem must never keep the watchdog from starting or stop it running.
/// </para>
/// <para>
/// There is deliberately no credential field of any kind, and there must never be one:
/// FortiClient holds the saved profile's credentials and this app never sees them.
/// </para>
/// </summary>
public sealed class GuiSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,

        // The file is small enough that people will hand-edit it, so be forgiving
        // about the two things they most often get wrong in JSON.
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Where the settings live on disk, so a caller can tell the user where to look.</summary>
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VpnWatchdog",
        "gui-settings.json");

    public string ProfileName { get; set; }
    public bool AutoReconnectEnabled { get; set; }
    public int ReconnectGracePeriodSeconds { get; set; }
    public int ReconnectMaxAttempts { get; set; }
    public int ReconnectInitialBackoffSeconds { get; set; }
    public int ReconnectMaxBackoffSeconds { get; set; }
    public int ReconnectVerifyTimeoutSeconds { get; set; }
    public int PollIntervalMs { get; set; }
    public bool StartMonitoringOnLaunch { get; set; }
    public bool MinimizeToTrayOnClose { get; set; }

    /// <summary>
    /// Built-in defaults, taken from <see cref="WatchdogConfig.Default"/> so the GUI and
    /// the CLI start from the same numbers.
    /// </summary>
    public GuiSettings()
    {
        WatchdogConfig defaults = WatchdogConfig.Default;

        ProfileName = defaults.ProfileName;

        // Core's default is false by design (see WatchdogConfig.Default for why), and
        // this is what a sparse or brand-new settings file inherits. Only the user
        // ticking the box in the dialog can ever turn it on.
        AutoReconnectEnabled = defaults.AutoReconnectEnabled;

        ReconnectGracePeriodSeconds = defaults.ReconnectGracePeriodSeconds;
        ReconnectMaxAttempts = defaults.ReconnectMaxAttempts;
        ReconnectInitialBackoffSeconds = defaults.ReconnectInitialBackoffSeconds;
        ReconnectMaxBackoffSeconds = defaults.ReconnectMaxBackoffSeconds;
        ReconnectVerifyTimeoutSeconds = defaults.ReconnectVerifyTimeoutSeconds;
        PollIntervalMs = defaults.PollIntervalMs;

        // Both off, so a first run behaves exactly as the GUI did before it had a
        // settings file: Start is a click, and closing the window exits.
        StartMonitoringOnLaunch = false;
        MinimizeToTrayOnClose = false;
    }

    /// <summary>
    /// Reads the settings file. Never throws: no file, unreadable file, or corrupt JSON
    /// all mean "defaults". Fields that are missing or null fall back individually,
    /// so a file that names only <c>ProfileName</c> is a perfectly good file - and one
    /// that says nothing about <c>AutoReconnectEnabled</c> leaves it off.
    /// </summary>
    public static GuiSettings Load()
    {
        var settings = new GuiSettings();

        StoredSettings? stored;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return settings;
            }

            stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath), SerializerOptions);
        }
        catch
        {
            // Deliberately broad: whatever is wrong with the file (syntax, a value of
            // the wrong type, a lock, permissions), the answer is the same - run on
            // defaults rather than refuse to run.
            return settings;
        }

        if (stored is null)
        {
            return settings;
        }

        if (!string.IsNullOrWhiteSpace(stored.ProfileName))
        {
            settings.ProfileName = stored.ProfileName;
        }

        settings.AutoReconnectEnabled = stored.AutoReconnectEnabled ?? settings.AutoReconnectEnabled;
        settings.ReconnectGracePeriodSeconds = stored.ReconnectGracePeriodSeconds ?? settings.ReconnectGracePeriodSeconds;
        settings.ReconnectMaxAttempts = stored.ReconnectMaxAttempts ?? settings.ReconnectMaxAttempts;
        settings.ReconnectInitialBackoffSeconds = stored.ReconnectInitialBackoffSeconds ?? settings.ReconnectInitialBackoffSeconds;
        settings.ReconnectMaxBackoffSeconds = stored.ReconnectMaxBackoffSeconds ?? settings.ReconnectMaxBackoffSeconds;
        settings.ReconnectVerifyTimeoutSeconds = stored.ReconnectVerifyTimeoutSeconds ?? settings.ReconnectVerifyTimeoutSeconds;
        settings.PollIntervalMs = stored.PollIntervalMs ?? settings.PollIntervalMs;
        settings.StartMonitoringOnLaunch = stored.StartMonitoringOnLaunch ?? settings.StartMonitoringOnLaunch;
        settings.MinimizeToTrayOnClose = stored.MinimizeToTrayOnClose ?? settings.MinimizeToTrayOnClose;

        settings.Clamp();
        return settings;
    }

    /// <summary>
    /// Writes the settings file, creating the folder if needed. Never throws; returns
    /// false if the file could not be written so the caller can say so.
    /// </summary>
    public bool Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-rename, so the file on disk is always either the previous
            // complete document or the new one. A half-written file would fail to
            // parse on the next Load and take EVERY setting back to defaults with it.
            string tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(tempPath, SettingsPath, overwrite: true);
            return true;
        }
        catch
        {
            // Read-only profile, full disk, locked file. Losing a preference is a
            // nuisance; nothing here is worth a crash.
            return false;
        }
    }

    /// <summary>
    /// The full runtime config: <see cref="WatchdogConfig.Default"/> with these values
    /// applied. Values are pulled into range on the way out as well as on load, so a
    /// caller that built or edited an instance by hand still hands the backend
    /// something sane (a zero poll interval, for one, would throw inside the timer).
    /// </summary>
    public WatchdogConfig ToWatchdogConfig()
    {
        GuiSettings safe = Clone();
        safe.Clamp();

        return WatchdogConfig.Default with
        {
            ProfileName = safe.ProfileName,
            PollIntervalMs = safe.PollIntervalMs,
            AutoReconnectEnabled = safe.AutoReconnectEnabled,
            ReconnectGracePeriodSeconds = safe.ReconnectGracePeriodSeconds,
            ReconnectMaxAttempts = safe.ReconnectMaxAttempts,
            ReconnectInitialBackoffSeconds = safe.ReconnectInitialBackoffSeconds,
            ReconnectMaxBackoffSeconds = safe.ReconnectMaxBackoffSeconds,
            ReconnectVerifyTimeoutSeconds = safe.ReconnectVerifyTimeoutSeconds,

            // Separate DB file from the CLI's, so both can run at the same time
            // without any SQLite write contention.
            DatabasePath = "vpn-watchdog-gui.db",
        };
    }

    /// <summary>A copy to edit without disturbing the live instance until the user confirms.</summary>
    public GuiSettings Clone() => (GuiSettings)MemberwiseClone();

    /// <summary>
    /// Pulls every value into the range the dialog offers (<see cref="GuiSettingLimits"/>).
    /// Nonsense degrades to "wait longer / try less", never to a rejected file, and
    /// never to a value a <see cref="NumericUpDown"/> cannot hold.
    /// </summary>
    private void Clamp()
    {
        ProfileName = string.IsNullOrWhiteSpace(ProfileName)
            ? WatchdogConfig.Default.ProfileName
            : ProfileName.Trim();

        PollIntervalMs = Math.Clamp(PollIntervalMs,
            GuiSettingLimits.MinPollIntervalMs, GuiSettingLimits.MaxPollIntervalMs);
        ReconnectGracePeriodSeconds = Math.Clamp(ReconnectGracePeriodSeconds,
            GuiSettingLimits.MinGracePeriodSeconds, GuiSettingLimits.MaxGracePeriodSeconds);
        ReconnectMaxAttempts = Math.Clamp(ReconnectMaxAttempts,
            GuiSettingLimits.MinReconnectAttempts, GuiSettingLimits.MaxReconnectAttempts);
        ReconnectInitialBackoffSeconds = Math.Clamp(ReconnectInitialBackoffSeconds,
            GuiSettingLimits.MinBackoffSeconds, GuiSettingLimits.MaxInitialBackoffSeconds);
        ReconnectMaxBackoffSeconds = Math.Clamp(ReconnectMaxBackoffSeconds,
            GuiSettingLimits.MinBackoffSeconds, GuiSettingLimits.MaxMaxBackoffSeconds);
        ReconnectVerifyTimeoutSeconds = Math.Clamp(ReconnectVerifyTimeoutSeconds,
            GuiSettingLimits.MinVerifyTimeoutSeconds, GuiSettingLimits.MaxVerifyTimeoutSeconds);

        // A cap below the starting value would make the "exponential" backoff shrink.
        // Same clamp ReconnectPolicy applies; done here too so the dialog shows the
        // numbers that will actually be used.
        ReconnectMaxBackoffSeconds = Math.Max(ReconnectMaxBackoffSeconds, ReconnectInitialBackoffSeconds);
    }

    /// <summary>
    /// On-disk shape. Every field nullable so a file may name only the settings it wants
    /// to change; anything absent or null keeps its default. Property names match
    /// <see cref="GuiSettings"/> exactly, so the object written by <see cref="Save"/>
    /// reads straight back through this.
    /// </summary>
    private sealed class StoredSettings
    {
        public string? ProfileName { get; set; }
        public bool? AutoReconnectEnabled { get; set; }
        public int? ReconnectGracePeriodSeconds { get; set; }
        public int? ReconnectMaxAttempts { get; set; }
        public int? ReconnectInitialBackoffSeconds { get; set; }
        public int? ReconnectMaxBackoffSeconds { get; set; }
        public int? ReconnectVerifyTimeoutSeconds { get; set; }
        public int? PollIntervalMs { get; set; }
        public bool? StartMonitoringOnLaunch { get; set; }
        public bool? MinimizeToTrayOnClose { get; set; }
    }
}

/// <summary>
/// The range each numeric setting may take. Shared by <see cref="GuiSettings"/> (the
/// clamp on load) and <see cref="SettingsForm"/> (each spinner's Minimum/Maximum) so
/// the two can never disagree: assigning a <see cref="NumericUpDown.Value"/> outside
/// its range throws, so a loaded value must always fit the control that displays it.
/// </summary>
internal static class GuiSettingLimits
{
    // Below half a second the probes themselves take longer than the interval.
    public const int MinPollIntervalMs = 500;
    public const int MaxPollIntervalMs = 60_000;

    public const int MinGracePeriodSeconds = 0;
    public const int MaxGracePeriodSeconds = 3_600;

    // At least one attempt: zero would be "auto-reconnect on, but never reconnect",
    // which the checkbox already expresses honestly.
    public const int MinReconnectAttempts = 1;
    public const int MaxReconnectAttempts = 100;

    // Never a zero backoff: a failed attempt must always buy FortiClient a pause
    // before the next one, however short.
    public const int MinBackoffSeconds = 1;
    public const int MaxInitialBackoffSeconds = 3_600;
    public const int MaxMaxBackoffSeconds = 86_400;

    public const int MinVerifyTimeoutSeconds = 1;
    public const int MaxVerifyTimeoutSeconds = 600;
}
