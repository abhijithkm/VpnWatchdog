using System.Net;
using VpnWatchdog.Core.Updates;
using Xunit;

namespace VpnWatchdog.Tests;

/// <summary>
/// <see cref="GitHubUpdateChecker"/> against a fake <see cref="HttpMessageHandler"/> - no
/// real network call in this file. The property under test throughout is the one stated
/// on <see cref="IUpdateChecker"/>: this must NEVER throw, whatever the server, the
/// network, or the response body does, because it is a background nicety that must never
/// be able to take anything down.
/// </summary>
public class GitHubUpdateCheckerTests
{
    private static readonly Version Current = new(1, 0, 3);

    private static GitHubUpdateChecker Checker(FakeHandler handler) =>
        new("owner", "repo", new HttpClient(handler));

    [Fact]
    public async Task CheckForUpdateAsync_WhenLatestTagIsNewer_ReturnsUpdateAvailableWithTagAndUrl()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"v1.2.0","html_url":"https://github.com/owner/repo/releases/tag/v1.2.0"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v1.2.0", result.LatestVersionTag);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.2.0", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenLatestTagEqualsCurrent_ReturnsUpToDate()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"v1.0.3","html_url":"https://example.invalid/v1.0.3"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenLatestTagIsOlder_ReturnsUpToDate_NeverClaimsAnUpdate()
    {
        // A stale/rolled-back "latest" on GitHub's side must never be reported as an
        // update - that would send someone "upgrading" to an older build.
        using var handler = FakeHandler.Json("""{"tag_name":"v0.9.0","html_url":"https://example.invalid/v0.9.0"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Theory]
    [InlineData("v2.0.0")]
    [InlineData("2.0.0")]
    public async Task CheckForUpdateAsync_AcceptsTagsWithOrWithoutALeadingV(string tag)
    {
        using var handler = FakeHandler.Json($$"""{"tag_name":"{{tag}}","html_url":"https://example.invalid/x"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenTagNameIsMissing_ReturnsCheckFailed_DoesNotThrow()
    {
        using var handler = FakeHandler.Json("""{"html_url":"https://example.invalid/x"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.CheckFailed, result.Outcome);
        Assert.Null(result.LatestVersionTag);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenTagNameIsNotAVersion_ReturnsCheckFailed_DoesNotThrow()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"not-a-version","html_url":"https://example.invalid/x"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.CheckFailed, result.Outcome);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseBodyIsMalformedJson_ReturnsCheckFailed_DoesNotThrow()
    {
        using var handler = FakeHandler.Json("this is not json");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.CheckFailed, result.Outcome);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenServerReturnsAnErrorStatus_ReturnsCheckFailed_DoesNotThrow()
    {
        using var handler = FakeHandler.Status(HttpStatusCode.NotFound);
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.CheckFailed, result.Outcome);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenTheHttpCallThrows_ReturnsCheckFailed_DoesNotThrow()
    {
        // Stands in for "no internet" / DNS failure / connection refused.
        using var handler = FakeHandler.Throwing(new HttpRequestException("simulated network failure"));
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.CheckFailed, result.Outcome);
    }

    // ------------------------------------------------------------------
    // ReleaseUrl is handed straight to Process.Start(UseShellExecute: true) by
    // the GUI when the user clicks the version label - these confirm a
    // response cannot smuggle anything else through it. Every case here still
    // reports UpdateAvailable with the real tag: a validation failure must
    // replace the URL with a known-safe fallback, never silently swallow a
    // genuine update the way a null/rejected ReleaseUrl would if the caller
    // required it non-null to show the notice at all.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckForUpdateAsync_WhenHtmlUrlHostIsNotGitHub_FallsBackToTheSafeReleasesUrl()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"v2.0.0","html_url":"https://evil.example/owner/repo/releases/tag/v2.0.0"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v2.0.0", result.LatestVersionTag);
        Assert.Equal("https://github.com/owner/repo/releases/latest", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenHtmlUrlIsNotHttps_FallsBackToTheSafeReleasesUrl()
    {
        // A non-https scheme handed to Process.Start(UseShellExecute: true) could
        // launch a local file or an arbitrary registered protocol handler.
        using var handler = FakeHandler.Json("""{"tag_name":"v2.0.0","html_url":"file:///C:/evil.exe"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("https://github.com/owner/repo/releases/latest", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenHtmlUrlIsForADifferentRepo_FallsBackToTheSafeReleasesUrl()
    {
        // Same host, but a different owner/repo path - defense in depth even
        // though a same-host redirect is not the likely attack shape here.
        using var handler = FakeHandler.Json("""{"tag_name":"v2.0.0","html_url":"https://github.com/someone-else/other-repo/releases/tag/v2.0.0"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("https://github.com/owner/repo/releases/latest", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenHtmlUrlIsMissing_StillReportsTheUpdate_WithTheSafeReleasesUrl()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"v2.0.0"}""");
        using var checker = Checker(handler);

        UpdateCheckResult result = await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v2.0.0", result.LatestVersionTag);
        Assert.Equal("https://github.com/owner/repo/releases/latest", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_RequestCarriesAUserAgent_GitHubRejectsRequestsWithout()
    {
        using var handler = FakeHandler.Json("""{"tag_name":"v1.0.3","html_url":"https://example.invalid/x"}""");
        using var checker = Checker(handler);

        await checker.CheckForUpdateAsync(Current, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.NotEmpty(handler.LastRequest!.Headers.UserAgent);
    }

    /// <summary>Records the one request it received and returns a canned response.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        private FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public static FakeHandler Json(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        public static FakeHandler Status(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

        public static FakeHandler Throwing(Exception ex) => new(_ => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
