using Microsoft.SemanticKernel;
using System.ComponentModel;
using NeuroSearch.Core;

namespace NeuroSearch.Plugins;

/// <summary>
/// SECURITY-HARDENED Web search plugin.
/// - Provider-neutral via <see cref="IWebSearchProvider"/>
/// - Centralized taint in <see cref="WebSearchTaint"/>
/// - ResearchDeeper = re-search multi-hop (NOT following scraped URLs)
/// </summary>
public class WebSearchPlugin
{
    private readonly IWebSearchProvider _provider;
    private readonly RateLimiter _rateLimiter;
    private readonly InjectionSessionState _session;
    private const int MaxResults = 10;

    public WebSearchPlugin(IWebSearchProvider provider, InjectionSessionState? session = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _session = session ?? new InjectionSessionState();
        _rateLimiter = new RateLimiter(requestsPerMinute: 10, burstSize: 5);
    }

    /// <summary>Legacy ctor for tests that mock HTTP against Serper JSON.</summary>
    public WebSearchPlugin(HttpClient httpClient, string apiKey, InjectionSessionState? session = null)
        : this(new SerperSearchProvider(httpClient, apiKey), session)
    {
    }

    public InjectionSessionState Session => _session;
    public string ProviderName => _provider.Name;

    [KernelFunction]
    [Description("Searches the internet for a given query and returns top search results with snippets. Use this when you need current information or facts from the web.")]
    public async Task<string> SearchAsync(
        [Description("The search query (e.g., 'Tesla stock price 2026', 'latest AI news')")]
        string query,
        [Description("Number of results to return (default: 5, max: 10)")]
        int numResults = 5,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteSearchAsync(query, numResults, hopDepth: 0, cancellationToken);
    }

    /// <summary>
    /// Multi-hop via RE-SEARCH: issue a NEW provider search for a topic/citation the agent
    /// identified. Safer than following a URL found in scraped HTML — the URL comes from
    /// the provider's ranking, not from attacker-controlled page text.
    /// Allowlist unchanged: scraped-page URLs remain non-fetchable.
    /// </summary>
    [KernelFunction]
    [Description(
        "Research a topic more deeply by issuing a NEW web search (not by following a URL from page text). " +
        "Use when you identified a paper, product, or citation name and need provider-ranked results. " +
        "Do NOT pass URLs scraped from pages — pass the topic/title to search for.")]
    public async Task<string> ResearchDeeperAsync(
        [Description("Topic, paper title, or citation name to search for (NOT a scraped URL)")]
        string topic,
        [Description("Number of results (default: 5, max: 10)")]
        int numResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (!_session.TryBeginResearchHop(out var hopReason))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[⚠️  SearchPlugin] {hopReason}");
            Console.ResetColor();
            return $"Error: {hopReason}";
        }

        var depth = _session.ResearchHopDepth;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[🔬 SearchPlugin] ResearchDeeper hop={depth} topic='{topic}'");
        Console.ResetColor();

        return await ExecuteSearchAsync(topic, numResults, hopDepth: depth, cancellationToken);
    }

    private async Task<string> ExecuteSearchAsync(
        string query,
        int numResults,
        int hopDepth,
        CancellationToken cancellationToken)
    {
        var validationResult = InputValidator.ValidateSearchQuery(query);
        if (!validationResult.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[❌ SearchPlugin] Validation failed: {validationResult.ErrorMessage}");
            Console.ResetColor();
            return $"Error: {validationResult.ErrorMessage}";
        }

        var sanitizedQuery = validationResult.Value!;

        // Query-exfil sink (defense-in-depth alongside InjectionPolicy)
        if (_session.LooksLikeSearchQueryExfiltration(sanitizedQuery))
        {
            var msg = "Blocked search: query looks like context exfiltration " +
                      "(trusted substring or high-entropy blob).";
            _session.LogBlock($"[SearchPlugin] {msg}");
            return $"Error: {msg}";
        }

        var rangeValidation = InputValidator.ValidateNumericRange(numResults, 1, MaxResults, "numResults");
        if (!rangeValidation.IsValid)
            return $"Error: {rangeValidation.ErrorMessage}";

        numResults = int.Parse(rangeValidation.Value!);

        var rateLimitResult = _rateLimiter.AllowRequest("search_api");
        if (!rateLimitResult.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[⚠️  SearchPlugin] Rate limited. Retry after {rateLimitResult.RetryAfterSeconds}s");
            Console.ResetColor();
            return $"Error: Rate limit exceeded. Please retry after {rateLimitResult.RetryAfterSeconds} seconds (HTTP 429).";
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(
            $"[🔍 SearchPlugin] provider={_provider.Name} hop={hopDepth} query='{sanitizedQuery}'...");
        Console.ResetColor();

        try
        {
            var result = await _provider.SearchAsync(sanitizedQuery, numResults, cancellationToken);
            var tainted = WebSearchTaint.FormatAndTaint(_session, result, hopDepth);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                $"[✓ SearchPlugin] provider={_provider.Name} results={result.Hits.Count} " +
                $"→ spotlighted Untrusted hop={hopDepth}");
            Console.ResetColor();

            return tainted;
        }
        catch (HttpRequestException ex)
        {
            return $"Search failed: {ex.Message}. Check your API key and internet connection.";
        }
        catch (Exception ex)
        {
            return $"Unexpected error during search: {ex.Message}";
        }
    }
}
