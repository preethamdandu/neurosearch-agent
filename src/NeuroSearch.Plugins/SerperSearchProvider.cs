using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NeuroSearch.Core;

namespace NeuroSearch.Plugins;

/// <summary>Serper.dev Google search backend. Does NOT taint — <see cref="WebSearchTaint"/> owns provenance.</summary>
public sealed class SerperSearchProvider : IWebSearchProvider
{
    private const string Endpoint = "https://google.serper.dev/search";
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public string Name => "serper";

    /// <summary>Last raw JSON body (for live-verify fixture reconciliation). Never log secrets from it.</summary>
    public string? LastRawJson { get; private set; }

    public SerperSearchProvider(HttpClient httpClient, string apiKey)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public async Task<WebSearchProviderResult> SearchAsync(
        string query,
        int numResults = 5,
        CancellationToken cancellationToken = default)
    {
        var request = new SearchRequest { Query = query, Num = numResults };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-API-KEY", _apiKey);

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        LastRawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = System.Text.Json.JsonSerializer.Deserialize<SerperResponse>(LastRawJson);

        var organic = payload?.Organic ?? Array.Empty<OrganicResult>();
        var hits = organic
            .Select((r, i) => new WebSearchHit(
                Title: r.Title ?? "",
                Url: r.Link ?? "",
                Snippet: r.Snippet ?? "",
                RawContent: null,
                Rank: i + 1,
                ProviderName: Name))
            .ToList();

        return new WebSearchProviderResult(hits, Name, query);
    }

    private sealed record SearchRequest
    {
        [JsonPropertyName("q")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("num")]
        public int Num { get; init; } = 5;
    }

    private sealed record SerperResponse(
        [property: JsonPropertyName("organic")] OrganicResult[]? Organic);

    private sealed record OrganicResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("snippet")] string? Snippet);
}
