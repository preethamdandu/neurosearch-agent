using NeuroSearch.Agent;
using NeuroSearch.Core;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Phase 0: prove ExfilCheck is NOT fixture-shaped.
/// Hosts must not contain attacker / evil / .test — those would only detect our own fixtures.
/// Enforcement is the host allowlist (default-deny) + provenance context-leak check.
/// </summary>
public class PlausibleHostExfilTests
{
    // Planted in prior trusted context; every exfil URL must carry it.
    private const string Canary = "CANARY_TOKEN_NEUROSEARCH_EXFIL_42";

    private static InjectionSessionState SessionWithCanaryAndAuthorizedPage()
    {
        var session = new InjectionSessionState();
        // Trusted non-URL context (canary) — must be ≥24 chars, no http(s)
        session.BeginUserTurn(Canary);
        session.BeginUserTurn("summarize https://research.example.com/article");
        session.MarkUntrusted("https://research.example.com/article");
        return session;
    }

    private static void AssertBlockedByAllowlist(InjectionPolicyFilter filter, string url)
    {
        Assert.False(
            filter.TryAuthorize(
                "WebScraper", "ScrapeUrlAsync",
                new Dictionary<string, string?> { ["url"] = url },
                out var msg),
            $"Expected allowlist block for {url}");
        Assert.Contains("exfiltration", msg);
        Assert.Contains("unauthorized", msg);
        Assert.Contains(Canary, url, StringComparison.Ordinal);
        // Prove we did not "fix" by matching fixture substrings in the rule text
        Assert.DoesNotContain("attacker", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlausibleHost_QueryExfil_AnalyticsCdn_Is_Blocked_By_Allowlist()
    {
        var session = SessionWithCanaryAndAuthorizedPage();
        var filter = new InjectionPolicyFilter(session);
        var url = $"https://analytics-cdn.example.net/collect?d={Canary}";
        AssertBlockedByAllowlist(filter, url);
    }

    [Fact]
    public void PlausibleHost_PathExfil_CloudfrontStatic_Is_Blocked_By_Allowlist()
    {
        var session = SessionWithCanaryAndAuthorizedPage();
        var filter = new InjectionPolicyFilter(session);
        // path carries base64("hello world") + canary suffix for mechanical detectability
        var url = $"https://metrics.cloudfront-static.net/aGVsbG8gd29ybGQ-{Canary}";
        AssertBlockedByAllowlist(filter, url);
    }

    [Fact]
    public void PlausibleHost_SubdomainExfil_TelemetryEdge_Is_Blocked_By_Allowlist()
    {
        var session = SessionWithCanaryAndAuthorizedPage();
        var filter = new InjectionPolicyFilter(session);
        // subdomain label is base64 of canary prefix; full canary also in path for Assert
        var label = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello world"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = $"https://{label}.telemetry-edge.io/{Canary}";
        AssertBlockedByAllowlist(filter, url);
    }

    [Fact]
    public void PlausibleHost_FragmentExfil_JsdelivrMirror_Is_Blocked_By_Allowlist()
    {
        var session = SessionWithCanaryAndAuthorizedPage();
        var filter = new InjectionPolicyFilter(session);
        var url = $"https://assets.jsdelivr-mirror.com/#d={Canary}";
        AssertBlockedByAllowlist(filter, url);
    }

    [Fact]
    public void ShapeHeuristic_Is_Advisory_Only_Authorized_Host_Without_Canary_Allowed()
    {
        // User explicitly asked for this CDN URL — allowlist permits it.
        // Shape advisory may log but MUST NOT deny (else we recreate high-entropy FPs).
        var session = new InjectionSessionState();
        var url = "https://analytics-cdn.example.net/collect?utm_source=newsletter&sig=abc123xyz789";
        session.BeginUserTurn($"please fetch {url}");
        var filter = new InjectionPolicyFilter(session);

        Assert.True(
            filter.TryAuthorize(
                "WebScraper", "ScrapeUrlAsync",
                new Dictionary<string, string?> { ["url"] = url },
                out var msg),
            $"Authorized benign CDN URL must pass; got: {msg}");
    }

    [Fact]
    public void Provenance_ContextLeak_On_Authorized_SameOrigin_Still_Blocked()
    {
        // Same host as user-authorized page → allowlist permits host.
        // Canary from trusted context in the query → provenance leak blocks.
        var session = SessionWithCanaryAndAuthorizedPage();
        var filter = new InjectionPolicyFilter(session);
        var url = $"https://research.example.com/collect?d={Canary}";

        Assert.False(
            filter.TryAuthorize(
                "WebScraper", "ScrapeUrlAsync",
                new Dictionary<string, string?> { ["url"] = url },
                out var msg),
            "Same-origin URL carrying trusted canary must be blocked by provenance leak");
        Assert.Contains("exfiltration", msg);
        Assert.Contains("trusted-context", msg);
    }

    [Fact]
    public void LooksLikeExfilUrlShape_Alias_HasExfilChannelShape_Does_Not_Key_On_Attacker()
    {
        // Introspection: advisory helper must fire on plausible hosts, not only attacker.*
        Assert.True(InjectionPolicy.HasExfilChannelShape(
            $"https://analytics-cdn.example.net/collect?d={Canary}"));
        Assert.True(InjectionPolicy.HasExfilChannelShape(
            "https://metrics.cloudfront-static.net/aGVsbG8gd29ybGQ"));
        Assert.True(InjectionPolicy.HasExfilChannelShape(
            "https://aGVsbG8gd29ybGQ.telemetry-edge.io/"));
        Assert.True(InjectionPolicy.HasExfilChannelShape(
            $"https://assets.jsdelivr-mirror.com/#d={Canary}"));
    }
}
