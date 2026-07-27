using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeuroSearch.Agent;
using NeuroSearch.Core;
using NeuroSearch.Plugins;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Documents the deliberate capability cost of the host allowlist:
/// the agent cannot follow links discovered inside scraped content.
/// </summary>
public class MultiHopCapabilityTests
{
    private const string PageUrl = "https://journal.example.com/paper-review";
    private const string CitedUrl = "https://arxiv.org/abs/1706.03762";

    private static string PageWithOutboundCitation() =>
        "<html><body><article>" +
        "<p>This review discusses attention mechanisms in sequence models.</p>" +
        $"<p>See also the original paper at {CitedUrl} for full details.</p>" +
        "</article></body></html>";

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task MultiHop_Follow_Link_Discovered_In_Scraped_Content_Is_Blocked()
    {
        var session = new InjectionSessionState(new SpotlightFormatter("multihop01"));
        var filter = new InjectionPolicyFilter(session);
        var scraper = new WebScraperPlugin(
            new HttpClient(new StubHandler(PageWithOutboundCitation())), session);

        // User only authorized the review page — not the cited arXiv URL
        session.BeginUserTurn($"summarize {PageUrl}");

        Assert.True(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = PageUrl }, out _));

        var scraped = await scraper.ScrapeUrlAsync(PageUrl);
        Assert.Contains("attention mechanisms", scraped);
        Assert.Contains(CitedUrl, scraped); // link is visible in extracted text

        // Agent attempts multi-hop: follow the citation discovered in content
        Assert.False(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = CitedUrl },
            out var msg),
            "Following a URL discovered only in scraped content must be blocked");
        Assert.Contains("unauthorized", msg);
        Assert.Contains("exfiltration", msg);
    }

    [Fact]
    public void EscapeHatch_User_Explicitly_Supplies_Second_Url_Allows_Follow()
    {
        // Escape hatch: user must paste the second URL into their message.
        // There is no confirm-dialog API — explicit supply IS the hatch.
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);

        session.BeginUserTurn($"summarize {PageUrl}");
        Assert.False(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = CitedUrl }, out _));

        // User explicitly supplies the follow-on URL
        session.BeginUserTurn($"now also fetch and summarize {CitedUrl}");
        Assert.True(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = CitedUrl },
            out var msg),
            $"User-supplied second URL must be allowed; got: {msg}");
    }

    [Fact]
    public void EscapeHatch_No_Implicit_Confirm_Api_Exists()
    {
        // Document: there is no ConfirmFollowUrl / ApproveHost API.
        // Presence of these names on the public surface would be a regression toward
        // auto-follow. This test locks the absence.
        var types = typeof(InjectionSessionState).Assembly.GetExportedTypes();
        foreach (var t in types)
        {
            foreach (var m in t.GetMethods())
            {
                var n = m.Name;
                Assert.False(
                    n.Contains("ConfirmFollow", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("ApproveHost", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("AllowDiscoveredUrl", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected escape-hatch API {t.Name}.{n} — multi-hop must stay user-supplied-URL only");
            }
        }
    }
}
