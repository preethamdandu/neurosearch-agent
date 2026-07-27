using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeuroSearch.Agent;
using NeuroSearch.Core;
using NeuroSearch.Plugins;
using Xunit;

namespace NeuroSearch.Tests;

public class WebSearchProviderTests
{
    private sealed class StubHandler(string body, string contentType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(r);
        }
    }

    private sealed class FakeProvider(string name, IReadOnlyList<WebSearchHit> hits) : IWebSearchProvider
    {
        public string Name => name;
        public Task<WebSearchProviderResult> SearchAsync(
            string query, int numResults = 5, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebSearchProviderResult(hits.Take(numResults).ToList(), name, query));
    }

    [Fact]
    public async Task SerperProvider_Maps_Organic_Fields()
    {
        var json = JsonSerializer.Serialize(new
        {
            organic = new[]
            {
                new { title = "T1", link = "https://a.example/1", snippet = "S1" },
                new { title = "T2", link = "https://b.example/2", snippet = "S2" }
            }
        });
        var handler = new StubHandler(json);
        var provider = new SerperSearchProvider(new HttpClient(handler), "test-key");
        var result = await provider.SearchAsync("q", 5);
        Assert.Equal("serper", result.ProviderName);
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("https://a.example/1", result.Hits[0].Url);
        Assert.Null(result.Hits[0].RawContent);
    }

    [Fact]
    public void TavilyFixture_Maps_RawContent_And_Snippet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tavily", "tavily_search_basic.json");
        Assert.True(File.Exists(path), $"missing fixture {path}");
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("tvly-", json);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);

        var result = TavilySearchProvider.FromFixtureJson(json, "attention is all you need");
        Assert.Equal(3, result.Hits.Count);
        Assert.NotNull(result.Hits[0].RawContent);
        Assert.Contains("Ignore previous instructions", result.Hits[1].Snippet);
    }

    [Fact]
    public async Task Centralized_Taint_Marks_All_Providers_Untrusted_And_Spotlit()
    {
        var providers = new IWebSearchProvider[]
        {
            new FakeProvider("serper",
            [
                new WebSearchHit("A", "https://a.example/1", "snippet A", null, 1, "serper")
            ]),
            new FakeProvider("tavily",
            [
                new WebSearchHit("B", "https://b.example/2", "snippet B",
                    "<html>raw</html>", 1, "tavily")
            ])
        };

        foreach (var p in providers)
        {
            var session = new InjectionSessionState(new SpotlightFormatter($"taint-{p.Name}"));
            var raw = await p.SearchAsync("q");
            var text = WebSearchTaint.FormatAndTaint(session, raw);
            Assert.True(session.HasUntrustedInContext, p.Name);
            Assert.Contains("<untrusted_web_content", text);
            Assert.Contains(session.SessionDelimiterId, text);
            Assert.True(session.IsProviderAuthorizedUrl(raw.Hits[0].Url), p.Name);
            if (raw.Hits[0].RawContent != null)
                Assert.Contains("raw", text);
        }
    }

    [Fact]
    public void Tavily_RawContent_Is_Untrusted_Like_Scraped_Html()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tavily", "tavily_search_basic.json");
        var result = TavilySearchProvider.FromFixtureJson(File.ReadAllText(path));
        var session = new InjectionSessionState(new SpotlightFormatter("raw01"));
        var text = WebSearchTaint.FormatAndTaint(session, result);
        Assert.Contains("RawContent:", text);
        Assert.Contains("<untrusted_web_content", text);
        Assert.True(session.HasUntrustedInContext);
        Assert.Contains("tavily:search", session.UntrustedOrigins[0]);
    }

    [Fact]
    public async Task ResearchDeeper_Completes_And_Tracks_Hop_Depth()
    {
        var hits = new List<WebSearchHit>
        {
            new("Paper", "https://arxiv.org/abs/1706.03762", "attention", null, 1, "fake")
        };
        var session = new InjectionSessionState(new SpotlightFormatter("hop01"));
        var plugin = new WebSearchPlugin(new FakeProvider("fake", hits), session);
        session.BeginUserTurn("research transformers");

        var r1 = await plugin.ResearchDeeperAsync("Attention Is All You Need");
        Assert.Contains("[research_hop=1]", r1);
        Assert.Equal(1, session.ResearchHopDepth);
        Assert.True(session.IsUrlAuthorized("https://arxiv.org/abs/1706.03762"));

        var r2 = await plugin.ResearchDeeperAsync("multi-head attention");
        Assert.Contains("[research_hop=2]", r2);
        Assert.Equal(2, session.ReSearchCountThisSession);
    }

    [Fact]
    public async Task ResearchDeeper_Depth_Ceiling_Enforced()
    {
        var hits = new List<WebSearchHit>
        {
            new("X", "https://example.com/x", "s", null, 1, "fake")
        };
        var session = new InjectionSessionState(new SpotlightFormatter("ceil01"))
        {
            MaxResearchHops = 2
        };
        var plugin = new WebSearchPlugin(new FakeProvider("fake", hits), session);
        Assert.DoesNotContain("Error", await plugin.ResearchDeeperAsync("a"));
        Assert.DoesNotContain("Error", await plugin.ResearchDeeperAsync("b"));
        var blocked = await plugin.ResearchDeeperAsync("c");
        Assert.Contains("ceiling", blocked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResearchDeeper_Canary_Query_Blocked_By_Exfil_Check()
    {
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);
        // Plant a trusted context canary via user message
        var canary = "SECRET_CONTEXT_TOKEN_ALPHA_BETA_GAMMA_99";
        session.BeginUserTurn($"Please remember this private note: {canary}");

        Assert.False(filter.TryAuthorize(
            "WebSearch", "ResearchDeeperAsync",
            new Dictionary<string, string?> { ["topic"] = $"search for {canary}" },
            out var msg));
        Assert.Contains("exfiltration", msg);
    }

    [Fact]
    public void ResearchDeeper_HighEntropy_Blob_In_Query_Blocked()
    {
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);
        session.BeginUserTurn("research transformers");
        var blob = "YmFzZTY0c211Z2dsZWRjb250ZXh0ZXhmaWx0ZXN0cGF5bG9hZHgz"; // >=32 b64-ish
        Assert.True(InjectionSessionState.ContainsLongHighEntropyToken($"find {blob}"));
        Assert.False(filter.TryAuthorize(
            "WebSearch", "SearchAsync",
            new Dictionary<string, string?> { ["query"] = $"find {blob}" },
            out var msg));
        Assert.Contains("exfiltration", msg);
    }

    [Fact]
    public async Task Scraped_Url_Still_Not_Fetchable_After_Provider_Search()
    {
        // Provider returns one URL; a different URL only in scraped HTML stays denied.
        var providerHits = new List<WebSearchHit>
        {
            new("Review", "https://journal.example.com/review", "see paper", null, 1, "fake")
        };
        var cited = "https://arxiv.org/abs/1706.03762";
        var page =
            $"<html><body><p>See also {cited}</p></body></html>";

        var session = new InjectionSessionState(new SpotlightFormatter("bound01"));
        var filter = new InjectionPolicyFilter(session);
        var search = new WebSearchPlugin(new FakeProvider("fake", providerHits), session);
        var scraper = new WebScraperPlugin(
            new HttpClient(new HtmlStub(page)), session);

        session.BeginUserTurn("summarize transformers via search");
        await search.SearchAsync("transformers");
        Assert.True(session.IsUrlAuthorized("https://journal.example.com/review"));

        // Scrape the provider-authorized URL — citation appears in body
        Assert.True(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = "https://journal.example.com/review" }, out _));
        var scraped = await scraper.ScrapeUrlAsync("https://journal.example.com/review");
        Assert.Contains(cited, scraped);

        // Citation from page text must STILL be blocked
        Assert.False(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = cited }, out var msg));
        Assert.Contains("unauthorized", msg);
    }

    [Fact]
    public void Seo_Poison_Rank1_Snippet_Still_Spotlit_And_Save_Blocked()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tavily", "tavily_search_basic.json");
        var result = TavilySearchProvider.FromFixtureJson(File.ReadAllText(path));
        // Hostile is rank 2 in fixture; re-rank as #1 for SEO-poison shape
        var poisoned = new WebSearchProviderResult(
            [
                result.Hits[1] with { Rank = 1 },
                result.Hits[0] with { Rank = 2 }
            ],
            "tavily",
            "transformer architecture");

        var session = new InjectionSessionState(new SpotlightFormatter("seo01"));
        var text = WebSearchTaint.FormatAndTaint(session, poisoned, hopDepth: 1);
        Assert.Contains("<untrusted_web_content", text);
        Assert.Contains("[research_hop=1]", text);
        Assert.True(session.HasUntrustedInContext);

        session.BeginUserTurn("research transformers"); // no save intent
        var filter = new InjectionPolicyFilter(session);
        Assert.False(filter.TryAuthorize(
            "VectorMemory", "SaveToMemoryAsync",
            new Dictionary<string, string?> { ["text"] = "CANARY_SAVE" },
            out var msg));
        Assert.Contains("tainted-sink", msg);
    }

    [Fact]
    public void ResearchDeeper_Consumes_Tool_Budget()
    {
        var session = new InjectionSessionState { MaxToolCallsPerTurn = 3, MaxToolCallsPerSession = 40 };
        var filter = new InjectionPolicyFilter(session);
        session.BeginUserTurn("go deeper on transformers");

        Assert.True(filter.TryAuthorize("WebSearch", "ResearchDeeperAsync",
            new Dictionary<string, string?> { ["topic"] = "transformers" }, out _));
        Assert.True(filter.TryAuthorize("WebSearch", "ResearchDeeperAsync",
            new Dictionary<string, string?> { ["topic"] = "attention" }, out _));
        Assert.True(filter.TryAuthorize("WebSearch", "ResearchDeeperAsync",
            new Dictionary<string, string?> { ["topic"] = "bert" }, out _));
        Assert.False(filter.TryAuthorize("WebSearch", "ResearchDeeperAsync",
            new Dictionary<string, string?> { ["topic"] = "gpt" }, out var msg));
        Assert.Contains("budget", msg, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HtmlStub(string body) : HttpMessageHandler
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
}
