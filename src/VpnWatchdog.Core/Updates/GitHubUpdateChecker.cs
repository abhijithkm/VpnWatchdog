using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VpnWatchdog.Core.Updates;

/// <summary>Outcome of one update check. Never distinguishes WHY a check failed -
/// no internet, GitHub down, malformed JSON and a timeout are all the same to a
/// caller deciding whether to show a notice: they just don't know right now.</summary>
public enum UpdateCheckOutcome
{
    UpdateAvailable,
    UpToDate,
    CheckFailed,
}

public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersionTag,
    string? ReleaseUrl);

/// <summary>
/// Checks GitHub's "latest release" API for a newer tagged version than the one
/// currently running. This is a background nicety, not a dependency of anything -
/// implementations must never throw and must never block noticeably longer than
/// their own internal timeout, so a slow or unreachable network can never delay
/// startup, the poll loop, or a one-shot CLI command even by a second.
/// </summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken ct);
}

/// <summary>
/// Compares the running assembly's <see cref="Version"/> (set via the .csproj
/// &lt;Version&gt; element - see the comment there) against the "tag_name" of
/// https://github.com/{owner}/{repo}/releases/latest. No credentials, no
/// telemetry sent - a single anonymous GET.
/// </summary>
public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    // Deliberately short: this must never be the reason startup feels slow.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _apiUrl;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _fallbackReleaseUrl;

    /// <param name="httpClient">
    /// Inject a fake for tests; omit to get a real one owned (and disposed) by
    /// this instance.
    /// </param>
    public GitHubUpdateChecker(string owner = "abhijithkm", string repo = "VpnWatchdog", HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        _owner = owner;
        _repo = repo;
        _apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        _fallbackReleaseUrl = $"https://github.com/{owner}/{repo}/releases/latest";
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();

        // GitHub's REST API rejects an unauthenticated request that has no
        // User-Agent at all, so this is not optional.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("VpnWatchdog-UpdateChecker");
        }
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
        {
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            GitHubReleaseResponse? release = await _http
                .GetFromJsonAsync<GitHubReleaseResponse>(_apiUrl, timeoutCts.Token)
                .ConfigureAwait(false);

            string? tag = release?.TagName;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.CheckFailed, null, null);
            }

            // Tags in this repo are "v1.0.3"; a bare "1.0.3" is tolerated too in
            // case that convention ever changes.
            string versionText = tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V')
                ? tag[1..]
                : tag;

            if (!Version.TryParse(versionText, out Version? latestVersion))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.CheckFailed, null, null);
            }

            UpdateCheckOutcome outcome = latestVersion > currentVersion
                ? UpdateCheckOutcome.UpdateAvailable
                : UpdateCheckOutcome.UpToDate;

            // The caller hands this straight to the shell (Process.Start) when
            // the user clicks the version label, so it must never be trusted
            // verbatim from the response - only a plain https://github.com/{owner}/{repo}/...
            // URL is accepted; anything else (a malformed value, an unexpected
            // host, or a missing html_url entirely) falls back to a known-safe
            // constant URL rather than either propagating something unvalidated
            // or silently dropping a genuine update notice.
            string releaseUrl = IsSafeReleaseUrl(release!.HtmlUrl) ? release.HtmlUrl! : _fallbackReleaseUrl;

            return new UpdateCheckResult(outcome, tag, releaseUrl);
        }
        catch
        {
            // No internet, DNS failure, GitHub unreachable/rate-limited, a
            // timeout, malformed JSON - deliberately one bucket. A background
            // version check must never surface as an error to the user.
            return new UpdateCheckResult(UpdateCheckOutcome.CheckFailed, null, null);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// True only for an absolute https URL on exactly github.com, under this
    /// checker's own owner/repo. Rejects everything else: a non-https scheme, a
    /// UNC/local path, a different host, or a malformed value - all of which
    /// would otherwise be handed to <c>Process.Start(UseShellExecute: true)</c>
    /// by the caller with no further check.
    /// </summary>
    private bool IsSafeReleaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;

        string expectedPrefix = $"/{_owner}/{_repo}/";
        return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
