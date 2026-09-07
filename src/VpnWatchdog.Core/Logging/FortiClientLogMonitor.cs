using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VpnWatchdog.Core.Logging;

/// <summary>
/// Tails FortiClient trace log files incrementally (tracked by per-file byte
/// offset), classifies newly-appended complete lines into <see cref="LogEvent"/>
/// records, and self-heals across log rotation/truncation. Read-only: never
/// opens a file for write, never shells out to any process.
///
/// <see cref="TailNewEventsAsync"/> performs exactly one pass over whatever new
/// bytes are currently available in each configured file and then completes -
/// it is the CALLER's responsibility to invoke it again on a poll timer.
/// </summary>
public sealed class FortiClientLogMonitor : IFortiClientLogMonitor
{
    // Defensive caps so a pathological, never-terminated log line cannot grow
    // in-memory state without bound while this process runs for days.
    private const int MaxPendingPartialChars = 262_144; // ~256K chars
    private const long MaxChunkBytesPerTick = 16 * 1024 * 1024; // 16MB per file per tick

    // --- Exact regex patterns per the Phase 1 log-format investigation. ---
    // Order matters: pattern 1 is checked before pattern 2 because pattern 2's
    // literal text also appears as a substring within some pattern-1 lines;
    // pattern 7 is only considered once none of patterns 1-6 matched.

    private static readonly Regex UnexpectedDisconnectRegex = new(
        "\"(?<profile>[^\"]*)\" disconnected unexpectedly!",
        RegexOptions.Compiled);

    private static readonly Regex PlainDisconnectedRegex = new(
        "\"(?<profile>[^\"]*)\" is disconnected\\.",
        RegexOptions.Compiled);

    private static readonly Regex ConnectedRegex = new(
        "\"(?<profile>[^\"]*)\" is connected\\.",
        RegexOptions.Compiled);

    private static readonly Regex DisconnectionReasonRegex = new(
        "disconnection reason: (?<code>\\d+), \\(\"(?<text>[^\"]*)\"\\)",
        RegexOptions.Compiled);

    private static readonly Regex DisconnectRequestRegex = new(
        "HandleTunnelDisconnectRequest\\(\\) \"(?<profile>[^\"]*)\" is going to be disconnected",
        RegexOptions.Compiled);

    private static readonly Regex NetworkChangeRegex = new(
        "HandleGlobalVPNEnvironmentChanged",
        RegexOptions.Compiled);

    private static readonly Regex GenericErrorRegex = new(
        "\\[FortiVPN\\s+\\d+\\s+error\\]",
        RegexOptions.Compiled);

    // Timestamp prefix, e.g. "[2026-09-04 01:26:43.2079114 UTC-04:00] ...".
    // The offset suffix is optional per the observed format.
    private static readonly Regex TimestampRegex = new(
        @"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)(?:\s+UTC(?<offset>[+-]\d{2}:\d{2}))?\]",
        RegexOptions.Compiled);

    private readonly string _logDirectory;
    private readonly string _profileName;
    private readonly IReadOnlyList<string> _fileNames;
    private readonly Dictionary<string, FileState> _fileStates;

    public FortiClientLogMonitor(string logDirectory, string profileName, IReadOnlyList<string>? fileNames = null)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        ArgumentNullException.ThrowIfNull(profileName);

        _logDirectory = logDirectory;
        _profileName = profileName;
        _fileNames = fileNames ?? new[] { "FortiVPN_1.log", "sslvpndaemon_1.log" };

        _fileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _fileNames)
        {
            _fileStates[name] = new FileState();
        }
    }

    public async IAsyncEnumerable<LogEvent> TailNewEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (string fileName in _fileNames)
        {
            ct.ThrowIfCancellationRequested();

            string path = Path.Combine(_logDirectory, fileName);

            // Requirement 1: file not present this tick -> skip, no exception.
            if (!File.Exists(path))
            {
                continue;
            }

            FileState state = _fileStates[fileName];

            long currentLength;
            DateTime creationUtc;
            try
            {
                var info = new FileInfo(path);
                currentLength = info.Length;
                creationUtc = info.CreationTimeUtc;
            }
            catch (IOException)
            {
                continue; // transient - self-heal by trying again next tick
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            // First time this file is ever observed (this process just started): skip
            // to end-of-file rather than backfilling the entire historical log. A
            // watchdog is expected to tail forward from the moment it starts, not
            // replay months of history (this file can be 5MB+ and span months) into
            // the event stream/database on every restart.
            if (!state.Initialized)
            {
                state.Offset = currentLength;
                state.LastLength = currentLength;
                state.LastCreationTimeUtc = creationUtc;
                state.PendingPartial = string.Empty;
                state.PendingRawTail = Array.Empty<byte>();
                state.Initialized = true;
                continue;
            }

            // Requirement 2: rotation/truncation detection -> reset and self-heal.
            // (A genuine rotation - unlike the first-ever observation above - DOES
            // reset to 0, since the new file's content truly is all "new".)
            if (creationUtc != state.LastCreationTimeUtc || currentLength < state.Offset)
            {
                state.Offset = 0;
                state.LastLength = 0;
                state.PendingPartial = string.Empty;
                state.PendingRawTail = Array.Empty<byte>();
            }

            state.LastCreationTimeUtc = creationUtc;

            // Requirement 3: nothing new since the last complete line we processed.
            if (currentLength <= state.Offset)
            {
                continue;
            }

            // `state.LastLength` is the file position already physically read (it
            // may sit AHEAD of `state.Offset` by the byte-length of the still-
            // pending partial line, which we already hold in memory and must not
            // re-read from disk - re-reading it while also prepending the buffer
            // would duplicate that text). Seeking here, rather than at
            // `state.Offset`, is what keeps the byte accounting exact.
            long seekPosition = state.LastLength;
            if (seekPosition < 0 || seekPosition > currentLength)
            {
                seekPosition = 0;
                state.PendingPartial = string.Empty;
                state.PendingRawTail = Array.Empty<byte>();
            }

            long bytesAvailable = currentLength - seekPosition;
            if (bytesAvailable <= 0)
            {
                continue;
            }

            int bytesToRead = (int)Math.Min(bytesAvailable, MaxChunkBytesPerTick);
            byte[] buffer = new byte[bytesToRead];
            int totalRead = 0;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(seekPosition, SeekOrigin.Begin);
                while (totalRead < bytesToRead)
                {
                    int read = await fs.ReadAsync(buffer.AsMemory(totalRead, bytesToRead - totalRead), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break; // shorter read than expected (e.g. concurrent truncation) - use what we got
                    }

                    totalRead += read;
                }
            }
            catch (IOException)
            {
                continue; // transient sharing violation - self-heal by retrying next tick
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (totalRead <= 0)
            {
                continue;
            }

            // Prepend whatever trailing bytes a PREVIOUS tick held back because they
            // were a UTF-8 sequence cut off mid-character at that tick's read
            // boundary - decoding them now, together with what follows, is what
            // decodes that character whole instead of as a lossy U+FFFD replacement.
            byte[] rawBytes;
            if (state.PendingRawTail.Length > 0)
            {
                rawBytes = new byte[state.PendingRawTail.Length + totalRead];
                Buffer.BlockCopy(state.PendingRawTail, 0, rawBytes, 0, state.PendingRawTail.Length);
                Buffer.BlockCopy(buffer, 0, rawBytes, state.PendingRawTail.Length, totalRead);
            }
            else
            {
                rawBytes = totalRead == buffer.Length ? buffer : buffer[..totalRead];
            }

            // THIS tick's read may itself end mid-character - hold those trailing
            // bytes back rather than decoding them now, so the same recovery applies
            // to them on the NEXT tick.
            int completeByteCount = FindCompleteUtf8Boundary(rawBytes, rawBytes.Length);
            state.PendingRawTail = rawBytes[completeByteCount..];

            string newText = Encoding.UTF8.GetString(rawBytes, 0, completeByteCount);
            string combined = state.PendingPartial + newText;
            string[] parts = combined.Split('\n');

            string newPendingPartial = parts[^1];
            if (newPendingPartial.Length > MaxPendingPartialChars)
            {
                // Pathological, never-terminated line: bound memory by dropping the
                // oldest excess. We will lose the prefix of this one line if it
                // ever completes, which is an acceptable trade-off for a watchdog
                // that must run unattended for days.
                newPendingPartial = newPendingPartial[^MaxPendingPartialChars..];
            }

            state.PendingPartial = newPendingPartial;
            state.LastLength = seekPosition + totalRead;
            state.Offset = state.LastLength - Encoding.UTF8.GetByteCount(newPendingPartial);

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if ((i & 0xFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                string rawPart = parts[i];
                string line = rawPart.EndsWith('\r') ? rawPart[..^1] : rawPart;
                if (line.Length == 0)
                {
                    continue;
                }

                LogEvent? logEvent = Classify(line, fileName);
                if (logEvent is not null)
                {
                    yield return logEvent;
                }
            }
        }
    }

    private LogEvent? Classify(string line, string sourceFile)
    {
        // Every profile comparison below is OrdinalIgnoreCase, matching
        // VpnEventCorrelator.ProfileMatches and every other profile comparison
        // in this app (e.g. against FortiClient's own GetTunnelList response):
        // a case difference between the configured profile name and what
        // FortiClient happens to log must never silently drop every log event
        // for the profile being watched.
        //
        // Pattern 1 must be checked before pattern 2: its literal text is a
        // substring of some pattern-1 lines, so once pattern 1 has matched we
        // must not also fall through and classify the same line as pattern 2.
        Match match = UnexpectedDisconnectRegex.Match(line);
        if (match.Success)
        {
            string profile = match.Groups["profile"].Value;
            if (!string.Equals(profile, _profileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new LogEvent(
                ParseTimestamp(line), profile, LogEventType.VpnDisconnectedUnexpectedly, line, null, null, sourceFile);
        }

        match = PlainDisconnectedRegex.Match(line);
        if (match.Success)
        {
            string profile = match.Groups["profile"].Value;
            if (!string.Equals(profile, _profileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new LogEvent(
                ParseTimestamp(line), profile, LogEventType.VpnDisconnected, line, null, null, sourceFile);
        }

        match = ConnectedRegex.Match(line);
        if (match.Success)
        {
            string profile = match.Groups["profile"].Value;
            if (!string.Equals(profile, _profileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new LogEvent(
                ParseTimestamp(line), profile, LogEventType.VpnConnected, line, null, null, sourceFile);
        }

        match = DisconnectionReasonRegex.Match(line);
        if (match.Success)
        {
            // No profile group in this line format - always emit, reason data is
            // valuable evidence on its own. Recorded verbatim, never interpreted.
            string code = match.Groups["code"].Value;
            string text = match.Groups["text"].Value;
            return new LogEvent(ParseTimestamp(line), null, LogEventType.Unknown, line, code, text, sourceFile);
        }

        match = DisconnectRequestRegex.Match(line);
        if (match.Success)
        {
            string profile = match.Groups["profile"].Value;
            if (!string.Equals(profile, _profileName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Ambiguous whether user- or recovery-driven - do not guess.
            return new LogEvent(ParseTimestamp(line), profile, LogEventType.Unknown, line, null, null, sourceFile);
        }

        if (NetworkChangeRegex.IsMatch(line))
        {
            return new LogEvent(ParseTimestamp(line), null, LogEventType.NetworkChange, line, null, null, sourceFile);
        }

        if (GenericErrorRegex.IsMatch(line))
        {
            return new LogEvent(ParseTimestamp(line), null, LogEventType.VpnError, line, null, null, sourceFile);
        }

        // Does not match any known category - deliberately do not yield an event
        // (avoid flooding the stream with Unknown for every info-level line).
        return null;
    }

    /// <summary>
    /// The number of leading bytes in <paramref name="buffer"/> (first <paramref
    /// name="length"/> of them) that form only WHOLE UTF-8 characters - i.e. where
    /// to cut so a multi-byte sequence straddling the end of a chunked disk read is
    /// never decoded half-present into a lossy U+FFFD replacement character.
    /// Walks back at most 3 bytes (the longest possible incomplete tail of a 4-byte
    /// UTF-8 sequence) looking for the lead byte of whatever sequence is open at
    /// the end of the buffer.
    /// </summary>
    private static int FindCompleteUtf8Boundary(byte[] buffer, int length)
    {
        int maxBack = Math.Min(3, length);
        for (int back = 1; back <= maxBack; back++)
        {
            byte b = buffer[length - back];

            // How many bytes a UTF-8 character starting with `b` should occupy in
            // total, or -1 if `b` is a continuation byte (10xxxxxx) rather than the
            // start of a character - keep walking back to find the real start.
            int expectedLength = (b & 0b1000_0000) == 0b0000_0000 ? 1
                : (b & 0b1110_0000) == 0b1100_0000 ? 2
                : (b & 0b1111_0000) == 0b1110_0000 ? 3
                : (b & 0b1111_1000) == 0b1111_0000 ? 4
                : -1;

            if (expectedLength == -1)
            {
                continue;
            }

            // Found the lead byte of the trailing character, `back` bytes from the
            // end. Complete only if the buffer actually holds all of its bytes.
            return expectedLength <= back ? length : length - back;
        }

        // No lead byte within the last 3 bytes: either a run of continuation bytes
        // whose lead is further back (and, at 4 bytes max per character, that
        // sequence must already be complete), or already-malformed input the
        // decoder's own replacement-character fallback can handle as before.
        return length;
    }

    private static DateTimeOffset ParseTimestamp(string line)
    {
        Match match = TimestampRegex.Match(line);
        if (!match.Success)
        {
            return DateTimeOffset.Now;
        }

        // Format is "yyyy-MM-dd HH:mm:ss.ffffff" possibly with more fractional
        // digits observed in practice (e.g. 7); "FFFFFFF" accepts either.
        if (!DateTime.TryParseExact(
                match.Groups["ts"].Value,
                "yyyy-MM-dd HH:mm:ss.FFFFFFF",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime localPart))
        {
            return DateTimeOffset.Now;
        }

        TimeSpan offset;
        Group offsetGroup = match.Groups["offset"];
        if (offsetGroup.Success && TimeSpan.TryParse(offsetGroup.Value, CultureInfo.InvariantCulture, out TimeSpan parsedOffset))
        {
            offset = parsedOffset;
        }
        else
        {
            // No offset present in this line - fall back to the local machine's
            // offset for that instant rather than fabricating a UTC assumption.
            offset = TimeZoneInfo.Local.GetUtcOffset(localPart);
        }

        try
        {
            return new DateTimeOffset(DateTime.SpecifyKind(localPart, DateTimeKind.Unspecified), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.Now;
        }
    }

    /// <summary>Per-file tailing state, persisted across repeated poll-tick calls.</summary>
    private sealed class FileState
    {
        /// <summary>File byte position marking the end of the last fully-processed (newline-terminated) line.</summary>
        public long Offset;

        /// <summary>File byte position already physically read off disk (may be ahead of <see cref="Offset"/> by the pending partial line's byte length).</summary>
        public long LastLength;

        public DateTime LastCreationTimeUtc;

        public string PendingPartial = string.Empty;

        /// <summary>
        /// Raw bytes physically read off disk but held back from decoding because they
        /// are the start of a multi-byte UTF-8 sequence cut off at the end of this
        /// tick's read - up to 3 bytes. Prepended to the NEXT tick's freshly-read bytes
        /// before decoding, so a character split across two reads is decoded whole
        /// instead of becoming a U+FFFD replacement character (which would otherwise
        /// corrupt <see cref="PendingPartial"/> and could make the eventual complete
        /// line fail to classify).
        /// </summary>
        public byte[] PendingRawTail = Array.Empty<byte>();

        /// <summary>True once this file has been observed at least once - distinguishes
        /// "first ever look at this file" (skip to end, do not backfill history) from a
        /// later genuine rotation (reset to 0, the new file's content truly is new).</summary>
        public bool Initialized;
    }
}
