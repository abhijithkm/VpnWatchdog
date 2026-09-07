using VpnWatchdog.Cli;
using VpnWatchdog.Core;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// <see cref="AppConfigLoader"/> against real temp files on disk - no fakes, since its
/// whole job is file/JSON handling. The property under test throughout: a config file
/// with a nonsensical value must degrade to a safe default, never crash the process or
/// wedge the poll loop. Regression tests for a bug where several numeric fields were
/// merged with no validation at all - see ClampPollIntervalMs and the Math.Max calls
/// in AppConfigLoader.Merge.
/// </summary>
public class AppConfigLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vpn-watchdog-test-config-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private WatchdogConfig LoadFromJson(string json)
    {
        File.WriteAllText(_path, json);
        return AppConfigLoader.Load(new[] { _path });
    }

    [Fact]
    public void Load_WithNoConfigFile_ReturnsDefaults()
    {
        WatchdogConfig result = AppConfigLoader.Load(new[] { Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json") });

        Assert.Equal(WatchdogConfig.Default.PollIntervalMs, result.PollIntervalMs);
        Assert.Equal(WatchdogConfig.Default.ProfileName, result.ProfileName);
    }

    [Theory]
    [InlineData(-1)] // would make Task.Delay(-1, ct) wait forever
    [InlineData(-100)] // would throw ArgumentOutOfRangeException from Task.Delay, uncaught
    [InlineData(0)] // would busy-spin the poll loop
    public void Load_WithAPollIntervalThatWouldWedgeOrCrashTheLoop_ClampsToTheSafeFloor(int badValue)
    {
        WatchdogConfig result = LoadFromJson($$"""{"PollIntervalMs": {{badValue}}}""");

        Assert.Equal(500, result.PollIntervalMs);
    }

    [Fact]
    public void Load_WithAPositivePollInterval_PassesItThroughUnchanged()
    {
        WatchdogConfig result = LoadFromJson("""{"PollIntervalMs": 5000}""");

        Assert.Equal(5000, result.PollIntervalMs);
    }

    [Theory]
    [InlineData(0)] // VpnEventCorrelator's ctor throws ArgumentOutOfRangeException on this, uncaught
    [InlineData(-5)]
    public void Load_WithAnOpenCorrelationTimeoutThatWouldCrashTheCorrelatorCtor_ClampsToOneMinute(int badValue)
    {
        WatchdogConfig result = LoadFromJson($$"""{"OpenCorrelationTimeoutMinutes": {{badValue}}}""");

        Assert.Equal(1, result.OpenCorrelationTimeoutMinutes);
    }

    [Theory]
    [InlineData(0)] // "auto-reconnect on, but can never attempt" - a switch that lies about what it does
    [InlineData(-3)]
    public void Load_WithAReconnectMaxAttemptsThatWouldNeverAttempt_ClampsToOne(int badValue)
    {
        WatchdogConfig result = LoadFromJson($$"""{"ReconnectMaxAttempts": {{badValue}}}""");

        Assert.Equal(1, result.ReconnectMaxAttempts);
    }

    [Fact]
    public void Load_WithAPartialOverrideFile_LeavesEverythingElseAtDefaults()
    {
        WatchdogConfig result = LoadFromJson("""{"ProfileName": "Some-Other-Profile"}""");

        Assert.Equal("Some-Other-Profile", result.ProfileName);
        Assert.Equal(WatchdogConfig.Default.ReconnectMaxAttempts, result.ReconnectMaxAttempts);
        Assert.Equal(WatchdogConfig.Default.PollIntervalMs, result.PollIntervalMs);
    }

    [Fact]
    public void Load_WithMalformedJson_FallsBackToDefaults_DoesNotThrow()
    {
        WatchdogConfig result = LoadFromJson("this is not json");

        Assert.Equal(WatchdogConfig.Default.PollIntervalMs, result.PollIntervalMs);
    }
}
