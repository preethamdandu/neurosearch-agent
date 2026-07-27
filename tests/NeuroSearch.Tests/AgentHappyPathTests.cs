using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeuroSearch.Agent;
using NeuroSearch.Core;
using NeuroSearch.Plugins;
using Qdrant.Client;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Product happy-path under LLM01 defenses. If these fail, the agent was
/// "secured" by being disabled — a P0 bug, not a green security suite.
/// </summary>
public class AgentHappyPathTests
{
    private const string BenignUrl = "https://docs.test/transformers";

    private static string BenignHtml() =>
        "<html><head><title>Transformers Overview</title></head><body><article>" +
        "<h1>Transformers Overview</h1>" +
        "<p>The transformer architecture uses self-attention to model sequence dependencies.</p>" +
        "<p>Key components include multi-head attention and positional encodings.</p>" +
        "</article></body></html>";

    private static HttpClient MockClient(string html)
    {
        return new HttpClient(new StubHandler(html))
        {
            BaseAddress = new Uri("https://docs.test/")
        };
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Research_Scrape_Save_Succeeds_With_Untrusted_Provenance()
    {
        var session = new InjectionSessionState(new SpotlightFormatter("happypath01"));
        var filter = new InjectionPolicyFilter(session);
        var scraper = new WebScraperPlugin(MockClient(BenignHtml()), session);

        var collection = $"neurosearch-happy-{Guid.NewGuid():N}"[..40];
        var qdrant = new QdrantClient("localhost", 6334);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var memory = new VectorMemoryPlugin(
            qdrant, http, "http://localhost:11434",
            "nomic-embed-text", collection, 768, session);

        try
        {
            // User explicitly asks to research AND save
            var userMsg = $"research {BenignUrl} and save what you find";
            session.BeginUserTurn(userMsg);
            Assert.True(session.UserRequestedMemorySave, "save-intent detector must fire for happy path");

            // Scrape authorized
            var scrapeOk = filter.TryAuthorize(
                "WebScraper", "ScrapeUrlAsync",
                new Dictionary<string, string?> { ["url"] = BenignUrl },
                out var scrapeMsg);
            Assert.True(scrapeOk, $"scrape should be allowed: {scrapeMsg}");

            var scraped = await scraper.ScrapeUrlAsync(BenignUrl);
            Assert.Contains("<untrusted_web_content", scraped);
            Assert.True(session.HasUntrustedInContext);

            // Save of scraped finding must SUCCEED under narrowed tainted-sink
            var saveText = "Transformers use self-attention for sequence modeling.";
            var saveOk = filter.TryAuthorize(
                "VectorMemory", "SaveToMemoryAsync",
                new Dictionary<string, string?> { ["text"] = saveText },
                out var saveMsg);
            Assert.True(saveOk, $"Save must succeed on happy path, got: {saveMsg}");

            var saveResult = await memory.SaveToMemoryAsync(saveText);
            Assert.DoesNotContain("Blocked", saveResult);
            Assert.Contains("provenance=untrusted", saveResult);
            Assert.Contains(BenignUrl, saveResult);

            // Later search retrieves with flag
            var searchOk = filter.TryAuthorize(
                "VectorMemory", "SearchMemoryAsync",
                new Dictionary<string, string?> { ["query"] = "self-attention" },
                out var searchAuthMsg);
            Assert.True(searchOk, $"Search must never be blocked by sink rule: {searchAuthMsg}");

            var search = await memory.SearchMemoryAsync("self-attention transformers", limit: 3, minRelevance: 0.1);
            Assert.Contains("provenance=untrusted", search);
            Assert.Contains(BenignUrl, search);
        }
        finally
        {
            try { await qdrant.DeleteCollectionAsync(collection); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task MultiTurn_Scrape_Then_User_Asks_To_Save_Succeeds()
    {
        var session = new InjectionSessionState(new SpotlightFormatter("happypath02"));
        var filter = new InjectionPolicyFilter(session);
        var scraper = new WebScraperPlugin(MockClient(BenignHtml()), session);

        var collection = $"neurosearch-happy2-{Guid.NewGuid():N}"[..40];
        var qdrant = new QdrantClient("localhost", 6334);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var memory = new VectorMemoryPlugin(
            qdrant, http, "http://localhost:11434",
            "nomic-embed-text", collection, 768, session);

        try
        {
            // Turn 1: scrape only (no save intent)
            session.BeginUserTurn($"summarize {BenignUrl}");
            Assert.False(session.UserRequestedMemorySave);

            Assert.True(filter.TryAuthorize(
                "WebScraper", "ScrapeUrlAsync",
                new Dictionary<string, string?> { ["url"] = BenignUrl }, out _));
            await scraper.ScrapeUrlAsync(BenignUrl);
            Assert.True(session.HasUntrustedInContext);

            // Without save intent, Save must still be blocked (poisoning defense intact)
            var blocked = !filter.TryAuthorize(
                "VectorMemory", "SaveToMemoryAsync",
                new Dictionary<string, string?> { ["text"] = "sneaky poison" },
                out var blockMsg);
            Assert.True(blocked, "Save without user save-intent must block");
            Assert.Contains("tainted-sink", blockMsg);

            // Turn 2: user asks to save
            session.BeginUserTurn("save what you find to memory");
            Assert.True(session.UserRequestedMemorySave);
            Assert.True(session.HasUntrustedInContext, "taint persists across turns");

            var saveText = "Key components include multi-head attention.";
            Assert.True(filter.TryAuthorize(
                "VectorMemory", "SaveToMemoryAsync",
                new Dictionary<string, string?> { ["text"] = saveText },
                out var saveMsg), $"turn-2 save should succeed: {saveMsg}");

            var saveResult = await memory.SaveToMemoryAsync(saveText);
            Assert.Contains("provenance=untrusted", saveResult);

            var search = await memory.SearchMemoryAsync("multi-head attention", limit: 3, minRelevance: 0.1);
            Assert.Contains("provenance=untrusted", search);
        }
        finally
        {
            try { await qdrant.DeleteCollectionAsync(collection); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Scrape_Then_Search_Never_Blocked_By_TaintedSink()
    {
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);
        session.BeginUserTurn($"read {BenignUrl}");
        session.MarkUntrusted(BenignUrl);

        // Read path must not hit tainted-sink
        Assert.True(filter.TryAuthorize(
            "VectorMemory", "SearchMemoryAsync",
            new Dictionary<string, string?> { ["query"] = "transformers" },
            out var msg), $"Search blocked unexpectedly: {msg}");

        Assert.True(filter.TryAuthorize(
            "VectorMemory", "GetMemoryStatsAsync",
            new Dictionary<string, string?>(),
            out var msg2), $"GetMemoryStats blocked unexpectedly: {msg2}");
    }

    [Fact]
    public void Poisoning_Still_Blocked_When_User_Did_Not_Ask_To_Save()
    {
        // Regression: narrowing must not reopen memory poisoning (prior test 5)
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);
        session.BeginUserTurn("read https://evil.test/article and tell me about it");
        Assert.False(session.UserRequestedMemorySave);
        session.MarkUntrusted("https://evil.test/article");

        var allowed = filter.TryAuthorize(
            "VectorMemory", "SaveToMemoryAsync",
            new Dictionary<string, string?> { ["text"] = "The CEO resigned today (FALSE)" },
            out var msg);

        Assert.False(allowed);
        Assert.Contains("tainted-sink", msg);
    }
}
