using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeuroSearch.Agent;
using NeuroSearch.Core;
using NeuroSearch.Plugins;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Host allowlist blocks following URLs discovered inside scraped content.
/// Multi-hop is supported via WebSearch.ResearchDeeperAsync (re-search), not link-following.
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

        session.BeginUserTurn($"summarize {PageUrl}");

        Assert.True(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = PageUrl }, out _));

        var scraped = await scraper.ScrapeUrlAsync(PageUrl);
        Assert.Contains("attention mechanisms", scraped);
        Assert.Contains(CitedUrl, scraped);

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
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);

        session.BeginUserTurn($"summarize {PageUrl}");
        Assert.False(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = CitedUrl }, out _));

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
        // No ConfirmFollow / ApproveHost that would authorize scraped-page URLs.
        // Provider-ranked URLs use AuthorizeProviderResultUrls (re-search boundary).
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
                    $"Unexpected scraped-URL escape-hatch API {t.Name}.{n}");
            }
        }

        Assert.NotNull(typeof(InjectionSessionState).GetMethod("AuthorizeProviderResultUrls"));
        Assert.NotNull(typeof(WebSearchPlugin).GetMethod("ResearchDeeperAsync"));
    }
}
