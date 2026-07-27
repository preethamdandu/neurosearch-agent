using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NeuroSearch.Core;

namespace NeuroSearch.Plugins;

/// <summary>
/// Tavily search backend (LLM-oriented retrieval). Default depth = basic.
/// Does NOT taint — <see cref="WebSearchTaint"/> owns provenance including RawContent.
/// </summary>
public sealed class TavilySearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://api.tavily.com/search";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _searchDepth;

    public string Name => "tavily";

    public TavilySearchProvider(HttpClient httpClient, string apiKey, string searchDepth = "basic")
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _searchDepth = string.IsNullOrWhiteSpace(searchDepth) ? "basic" : searchDepth;
    }

    public async Task<WebSearchProviderResult> SearchAsync(
        string query,
        int numResults = 5,
        CancellationToken cancellationToken = default)
    {
        var request = new TavilyRequest
        {
            ApiKey = _apiKey,
            Query = query,
            MaxResults = numResults,
            SearchDepth = _searchDepth,
            IncludeRawContent = true
        };

        using var response = await _http.PostAsJsonAsync(Endpoint, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TavilyResponse>(
            cancellationToken: cancellationToken);

        var results = payload?.Results ?? Array.Empty<TavilyResult>();
        var hits = results
            .Select((r, i) => new WebSearchHit(
                Title: r.Title ?? "",
                Url: r.Url ?? "",
                Snippet: r.Content ?? "",
                RawContent: string.IsNullOrWhiteSpace(r.RawContent) ? null : r.RawContent,
                Rank: i + 1,
                ProviderName: Name))
            .ToList();

        return new WebSearchProviderResult(hits, Name, query);
    }

    /// <summary>Map a recorded Tavily JSON fixture into the neutral type (unit tests).</summary>
    public static WebSearchProviderResult FromFixtureJson(string json, string query = "fixture")
    {
        var payload = System.Text.Json.JsonSerializer.Deserialize<TavilyResponse>(json)
            ?? new TavilyResponse(null);
        var results = payload.Results ?? Array.Empty<TavilyResult>();
        var hits = results
            .Select((r, i) => new WebSearchHit(
                Title: r.Title ?? "",
                Url: r.Url ?? "",
                Snippet: r.Content ?? "",
                RawContent: string.IsNullOrWhiteSpace(r.RawContent) ? null : r.RawContent,
                Rank: i + 1,
                ProviderName: "tavily"))
            .ToList();
        return new WebSearchProviderResult(hits, "tavily", query);
    }

    private sealed class TavilyRequest
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; init; } = "";

        [JsonPropertyName("query")]
        public string Query { get; init; } = "";

        [JsonPropertyName("max_results")]
        public int MaxResults { get; init; } = 5;

        [JsonPropertyName("search_depth")]
        public string SearchDepth { get; init; } = "basic";

        [JsonPropertyName("include_raw_content")]
        public bool IncludeRawContent { get; init; } = true;
    }

    private sealed record TavilyResponse(
        [property: JsonPropertyName("results")] TavilyResult[]? Results);

    private sealed record TavilyResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("raw_content")] string? RawContent,
        [property: JsonPropertyName("score")] double? Score);
}
