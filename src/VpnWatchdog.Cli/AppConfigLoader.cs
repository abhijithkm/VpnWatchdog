using System.Text.Json;
using System.Text.Json.Serialization;
using VpnWatchdog.Core;

namespace VpnWatchdog.Cli;

/// <summary>
/// Loads a <see cref="WatchdogConfig"/> for the CLI. Deliberately does NOT require every
/// field to be present in the config file - users typically only want to override a
/// handful of values (e.g. profile name or probe URL), so any field that is missing or
/// explicitly null in the JSON falls back to <see cref="WatchdogConfig.Default"/>.
/// </summary>
public static class AppConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Resolves and loads config: the first CLI arg (if given) is treated as an explicit
    /// path to a JSON config file; otherwise "appsettings.json" next to the executable is
    /// used if it exists. If no config file can be found at all, <see cref="WatchdogConfig.Default"/>
    /// is returned untouched.
    /// </summary>
    public static WatchdogConfig Load(string[] args)
    {
        string configPath = ResolveConfigPath(args);

        if (!File.Exists(configPath))
        {
            return WatchdogConfig.Default;
        }

        try
        {
            string json = File.ReadAllText(configPath);
            var overrides = JsonSerializer.Deserialize<WatchdogConfigOverrides>(json, SerializerOptions);
            return Merge(WatchdogConfig.Default, overrides);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Warning: could not read config file '{configPath}' ({ex.Message}). " +
                "Falling back to built-in defaults.");
            return WatchdogConfig.Default;
        }
    }

    private static string ResolveConfigPath(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return args[0];
        }

        return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    /// <summary>
    /// Manually merges each field individually rather than assuming the JSON document is
    /// complete - a partially-specified override file must still produce a fully valid
    /// <see cref="WatchdogConfig"/>.
    /// </summary>
    private static WatchdogConfig Merge(WatchdogConfig defaults, WatchdogConfigOverrides? overrides)
    {
        if (overrides is null)
        {
            return defaults;
        }

        return new WatchdogConfig(
            ProfileName: NonEmptyOr(overrides.ProfileName, defaults.ProfileName),
            AdapterDescriptionPattern: NonEmptyOr(overrides.AdapterDescriptionPattern, defaults.AdapterDescriptionPattern),
            HttpsProbeUrl: NonEmptyOr(overrides.HttpsProbeUrl, defaults.HttpsProbeUrl),
            PollIntervalMs: overrides.PollIntervalMs ?? defaults.PollIntervalMs,
            HeartbeatIntervalMs: overrides.HeartbeatIntervalMs ?? defaults.HeartbeatIntervalMs,
            OpenCorrelationTimeoutMinutes: overrides.OpenCorrelationTimeoutMinutes ?? defaults.OpenCorrelationTimeoutMinutes,
            FortiClientLogDirectory: NonEmptyOr(overrides.FortiClientLogDirectory, defaults.FortiClientLogDirectory),
            DatabasePath: NonEmptyOr(overrides.DatabasePath, defaults.DatabasePath),
            AutoReconnectEnabled: overrides.AutoReconnectEnabled ?? defaults.AutoReconnectEnabled,
            ReconnectGracePeriodSeconds: overrides.ReconnectGracePeriodSeconds ?? defaults.ReconnectGracePeriodSeconds,
            ReconnectMaxAttempts: overrides.ReconnectMaxAttempts ?? defaults.ReconnectMaxAttempts,
            ReconnectInitialBackoffSeconds: overrides.ReconnectInitialBackoffSeconds ?? defaults.ReconnectInitialBackoffSeconds,
            ReconnectMaxBackoffSeconds: overrides.ReconnectMaxBackoffSeconds ?? defaults.ReconnectMaxBackoffSeconds,
            ReconnectVerifyTimeoutSeconds: overrides.ReconnectVerifyTimeoutSeconds ?? defaults.ReconnectVerifyTimeoutSeconds,
            ActivityLogPath: NonEmptyOr(overrides.ActivityLogPath, defaults.ActivityLogPath),
            ActivityLogMaxEntries: overrides.ActivityLogMaxEntries ?? defaults.ActivityLogMaxEntries);
    }

    private static string NonEmptyOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Every field nullable/optional by design so a config file can specify only the
    /// fields it wants to override. Property names match <see cref="WatchdogConfig"/>
    /// exactly (matched case-insensitively on read).
    /// </summary>
    private sealed class WatchdogConfigOverrides
    {
        [JsonPropertyName("ProfileName")]
        public string? ProfileName { get; set; }

        [JsonPropertyName("AdapterDescriptionPattern")]
        public string? AdapterDescriptionPattern { get; set; }

        [JsonPropertyName("HttpsProbeUrl")]
        public string? HttpsProbeUrl { get; set; }

        [JsonPropertyName("PollIntervalMs")]
        public int? PollIntervalMs { get; set; }

        [JsonPropertyName("HeartbeatIntervalMs")]
        public int? HeartbeatIntervalMs { get; set; }

        [JsonPropertyName("OpenCorrelationTimeoutMinutes")]
        public int? OpenCorrelationTimeoutMinutes { get; set; }

        [JsonPropertyName("FortiClientLogDirectory")]
        public string? FortiClientLogDirectory { get; set; }

        [JsonPropertyName("DatabasePath")]
        public string? DatabasePath { get; set; }

        // Reconnect settings. AutoReconnectEnabled stays false unless a config file
        // (or --auto-reconnect) explicitly turns it on - it must never become true
        // by accident, because no log signal distinguishes a deliberate manual
        // disconnect from an unexpected drop.
        [JsonPropertyName("AutoReconnectEnabled")]
        public bool? AutoReconnectEnabled { get; set; }

        [JsonPropertyName("ReconnectGracePeriodSeconds")]
        public int? ReconnectGracePeriodSeconds { get; set; }

        [JsonPropertyName("ReconnectMaxAttempts")]
        public int? ReconnectMaxAttempts { get; set; }

        [JsonPropertyName("ReconnectInitialBackoffSeconds")]
        public int? ReconnectInitialBackoffSeconds { get; set; }

        [JsonPropertyName("ReconnectMaxBackoffSeconds")]
        public int? ReconnectMaxBackoffSeconds { get; set; }

        [JsonPropertyName("ReconnectVerifyTimeoutSeconds")]
        public int? ReconnectVerifyTimeoutSeconds { get; set; }

        // Activity log. The default path is deliberately FIXED and shared with the
        // GUI so both front-ends append to ONE history; overriding it here splits
        // that history in two, which is why it is left out of the example config.
        [JsonPropertyName("ActivityLogPath")]
        public string? ActivityLogPath { get; set; }

        [JsonPropertyName("ActivityLogMaxEntries")]
        public int? ActivityLogMaxEntries { get; set; }
    }
}
