using System.Text;
using VpnWatchdog.Core;
using VpnWatchdog.Core.Logging;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// <see cref="FortiClientLogMonitor"/> against real temp files on disk - its whole job
/// is file/byte-offset/encoding handling, so a fake would test nothing. Each test drives
/// the SAME file name the monitor expects ("FortiVPN_1.log") inside a fresh temp
/// directory, appending bytes between calls to <c>TailNewEventsAsync</c> exactly the way
/// a poll timer would.
/// </summary>
public class FortiClientLogMonitorTests : IDisposable
{
    private const string Profile = "MLA-DEV-VPN-2-LocalAuth";
    private const string LogFileName = "FortiVPN_1.log";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vpn-watchdog-test-logs-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_dir, LogFileName);

    public FortiClientLogMonitorTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FortiClientLogMonitor Monitor(string profileName = Profile) =>
        new(_dir, profileName, new[] { LogFileName });

    private static async Task<List<LogEvent>> DrainAsync(IFortiClientLogMonitor monitor)
    {
        var events = new List<LogEvent>();
        await foreach (LogEvent e in monitor.TailNewEventsAsync(CancellationToken.None))
        {
            events.Add(e);
        }
        return events;
    }

    private void AppendRaw(byte[] bytes)
    {
        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes, 0, bytes.Length);
    }

    private void Append(string text) => AppendRaw(Encoding.UTF8.GetBytes(text));

    // ------------------------------------------------------------------
    // 1. First-ever observation of an existing file must skip to end (no
    //    backfill), even when the file already has complete lines in it.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TailNewEventsAsync_OnFirstObservation_SkipsExistingContent_DoesNotBackfill()
    {
        File.WriteAllText(LogPath, $"[2026-09-04 08:00:00.0000000 UTC-04:00] \"{Profile}\" is connected.\n", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();

        List<LogEvent> events = await DrainAsync(monitor);

        Assert.Empty(events);
    }

    // ------------------------------------------------------------------
    // 2. A line appended AFTER the first observation is classified normally.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TailNewEventsAsync_ForALineAppendedAfterFirstObservation_ClassifiesIt()
    {
        File.WriteAllText(LogPath, "", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();
        await DrainAsync(monitor); // establishes the initial "skip to end" baseline

        Append($"[2026-09-04 08:00:00.0000000 UTC-04:00] \"{Profile}\" is connected.\n");
        List<LogEvent> events = await DrainAsync(monitor);

        LogEvent e = Assert.Single(events);
        Assert.Equal(LogEventType.VpnConnected, e.EventType);
        Assert.Equal(Profile, e.ProfileName);
    }

    [Fact]
    public async Task TailNewEventsAsync_ForAnUnexpectedDisconnectLine_ClassifiesItWithReasonCodeAndText()
    {
        File.WriteAllText(LogPath, "", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();
        await DrainAsync(monitor);

        Append(
            $"[2026-09-04 08:00:10.0000000 UTC-04:00] [FortiVPN  2432   error] " +
            $"!!! fortivpn::StateMachine::HandleTunnelDisconnected session 1 (.\\user) " +
            $"\"{Profile}\" disconnected unexpectedly!\n" +
            "[2026-09-04 08:00:10.1000000 UTC-04:00] disconnection reason: 21, (\"Cancelled\")\n");

        List<LogEvent> events = await DrainAsync(monitor);

        Assert.Equal(2, events.Count);
        Assert.Equal(LogEventType.VpnDisconnectedUnexpectedly, events[0].EventType);
        Assert.Equal(Profile, events[0].ProfileName);

        // DisconnectionReasonRegex's line format carries no profile field at all -
        // see VpnEventCorrelator.ClassifyNearbyDisconnect's own comment on why.
        Assert.Equal(LogEventType.Unknown, events[1].EventType);
        Assert.Null(events[1].ProfileName);
        Assert.Equal("21", events[1].ReasonCode);
        Assert.Equal("Cancelled", events[1].ReasonText);
    }

    // ------------------------------------------------------------------
    // 3. Profile matching is case-insensitive, matching every other profile
    //    comparison in this app (VpnEventCorrelator.ProfileMatches, the
    //    GetTunnelList checks). Regression test for a bug where this one
    //    comparison was ordinal, silently dropping evidence for a profile
    //    name that differs only in casing from the config file.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TailNewEventsAsync_MatchesTheConfiguredProfile_CaseInsensitively()
    {
        File.WriteAllText(LogPath, "", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor(profileName: Profile.ToUpperInvariant());
        await DrainAsync(monitor);

        Append($"[2026-09-04 08:00:00.0000000 UTC-04:00] \"{Profile}\" is connected.\n");
        List<LogEvent> events = await DrainAsync(monitor);

        LogEvent e = Assert.Single(events);
        Assert.Equal(LogEventType.VpnConnected, e.EventType);
    }

    [Fact]
    public async Task TailNewEventsAsync_ForADifferentProfile_YieldsNothing()
    {
        File.WriteAllText(LogPath, "", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();
        await DrainAsync(monitor);

        Append("[2026-09-04 08:00:00.0000000 UTC-04:00] \"MLA-DEV-VPN-1-SSO\" is connected.\n");
        List<LogEvent> events = await DrainAsync(monitor);

        Assert.Empty(events);
    }

    // ------------------------------------------------------------------
    // 4. A multi-byte UTF-8 character split across two separate reads (a real
    //    possibility: FortiVPN_1.log is appended to live while this tails it)
    //    must decode whole once the rest arrives, never as a replacement
    //    character. Regression test for FindCompleteUtf8Boundary.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TailNewEventsAsync_WhenAMultiByteCharacterIsSplitAcrossTwoReads_DecodesItWhole()
    {
        File.WriteAllText(LogPath, "", Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();
        await DrainAsync(monitor);

        // U+2014 EM DASH is 3 bytes in UTF-8 (0xE2 0x80 0x94) - a real example of
        // the kind of stray non-ASCII byte FortiClient's log has been observed to
        // contain. The line is split so the first read ends with only the lead
        // byte of the dash present.
        const string line = "note—worthy \"" + Profile + "\" is connected.\n";
        byte[] fullBytes = Encoding.UTF8.GetBytes(line);

        int dashLeadByteIndex = Array.IndexOf(fullBytes, (byte)0xE2);
        Assert.True(dashLeadByteIndex > 0, "test line must contain the em-dash's lead byte");
        int splitAfter = dashLeadByteIndex + 1; // include only the lead byte, not its 2 continuation bytes

        AppendRaw(fullBytes[..splitAfter]);
        List<LogEvent> firstPass = await DrainAsync(monitor);
        Assert.Empty(firstPass); // no complete line yet - the dash's tail is what's pending

        AppendRaw(fullBytes[splitAfter..]);
        List<LogEvent> secondPass = await DrainAsync(monitor);

        LogEvent e = Assert.Single(secondPass);
        Assert.Equal(LogEventType.VpnConnected, e.EventType);
        Assert.Contains("note—worthy", e.RawLine);
        Assert.DoesNotContain('�', e.RawLine);
    }

    // ------------------------------------------------------------------
    // 5. Log rotation (a new file created in place, e.g. smaller than before)
    //    resets and self-heals rather than throwing or getting stuck.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TailNewEventsAsync_AfterLogRotation_SelfHealsAndClassifiesTheNewFilesContent()
    {
        File.WriteAllText(LogPath, new string('x', 500), Encoding.UTF8);
        FortiClientLogMonitor monitor = Monitor();
        await DrainAsync(monitor); // baseline against the pre-rotation file

        // Simulate rotation: delete and recreate with fresh (shorter) content and a
        // new creation time, exactly what FortiClient's own log rotation does.
        File.Delete(LogPath);
        await Task.Delay(10); // ensure a distinguishable CreationTimeUtc on some filesystems
        File.WriteAllText(LogPath, $"[2026-09-04 09:00:00.0000000 UTC-04:00] \"{Profile}\" is connected.\n", Encoding.UTF8);

        List<LogEvent> events = await DrainAsync(monitor);

        LogEvent e = Assert.Single(events);
        Assert.Equal(LogEventType.VpnConnected, e.EventType);
    }
}
