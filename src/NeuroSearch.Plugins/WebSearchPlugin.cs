using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NeuroSearch.Core;

namespace NeuroSearch.Plugins;

/// <summary>
/// SECURITY-HARDENED Web search plugin
/// - Input validation (SQL injection, XSS protection)
/// - Rate limiting (10 req/min with burst of 5)
/// - Secure API key handling (env variables only)
/// - Graceful error handling
/// </summary>
public class WebSearchPlugin
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly RateLimiter _rateLimiter;
    private const string SerperEndpoint = "https://google.serper.dev/search";
    private const int MaxResults = 10;

    public WebSearchPlugin(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        
        // Rate limit: 10 requests/minute with burst of 5
        _rateLimiter = new RateLimiter(requestsPerMinute: 10, burstSize: 5);
    }

    [KernelFunction]
    [Description("Searches the internet for a given query and returns top search results with snippets. Use this when you need current information or facts from the web.")]
    public async Task<string> SearchAsync(
        [Description("The search query (e.g., 'Tesla stock price 2026', 'latest AI news')")] 
        string query,
        [Description("Number of results to return (default: 5, max: 10)")]
        int numResults = 5,
        CancellationToken cancellationToken = default)
    {
        // SECURITY: Input validation
        var validationResult = InputValidator.ValidateSearchQuery(query);
        if (!validationResult.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[❌ SearchPlugin] Validation failed: {validationResult.ErrorMessage}");
            Console.ResetColor();
            return $"Error: {validationResult.ErrorMessage}";
        }

        // Use sanitized query
        var sanitizedQuery = validationResult.Value;

        // SECURITY: Validate numeric range
        var rangeValidation = InputValidator.ValidateNumericRange(numResults, 1, MaxResults, "numResults");
        if (!rangeValidation.IsValid)
            return $"Error: {rangeValidation.ErrorMessage}";

        numResults = int.Parse(rangeValidation.Value);

        // SECURITY: Rate limiting
        var rateLimitResult = _rateLimiter.AllowRequest("search_api");
        if (!rateLimitResult.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[⚠️  SearchPlugin] Rate limited. Retry after {rateLimitResult.RetryAfterSeconds}s");
            Console.ResetColor();
            return $"Error: Rate limit exceeded. Please retry after {rateLimitResult.RetryAfterSeconds} seconds (HTTP 429).";
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"[🔍 SearchPlugin] Searching web for: '{sanitizedQuery}'...");
        Console.ResetColor();

        try
        {
            var request = new SearchRequest { Query = sanitizedQuery, Num = numResults };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, SerperEndpoint)
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("X-API-KEY", _apiKey);

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SerperResponse>(cancellationToken: cancellationToken);

            if (result?.Organic == null || result.Organic.Length == 0)
                return "No results found for this query.";

            // Format results for LLM consumption
            var formatted = string.Join("\n\n", result.Organic.Select((r, i) =>
                $"Result {i + 1}:\n" +
                $"Title: {r.Title}\n" +
                $"Snippet: {r.Snippet}\n" +
                $"URL: {r.Link}"));

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓ SearchPlugin] Found {result.Organic.Length} results");
            Console.ResetColor();

            return formatted;
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

    // Internal models for Serper API
    private record SearchRequest
    {
        [JsonPropertyName("q")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("num")]
        public int Num { get; init; } = 5;
    }

    private record SerperResponse(
        [property: JsonPropertyName("organic")] OrganicResult[] Organic);

    private record OrganicResult(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("snippet")] string Snippet);
}
