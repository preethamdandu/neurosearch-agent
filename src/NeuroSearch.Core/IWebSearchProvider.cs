namespace NeuroSearch.Core;

/// <summary>Provider-neutral web search hit.</summary>
public sealed record WebSearchHit(
    string Title,
    string Url,
    string Snippet,
    string? RawContent,
    int Rank,
    string ProviderName);

/// <summary>Result of a provider search (pre-taint).</summary>
public sealed record WebSearchProviderResult(
    IReadOnlyList<WebSearchHit> Hits,
    string ProviderName,
    string Query,
    string? ProviderAnswer = null);

/// <summary>
/// Web search backend. Implementations must NOT spotlight/taint —
/// <see cref="WebSearchTaint"/> owns that so no provider can forget.
/// </summary>
public interface IWebSearchProvider
{
    string Name { get; }

    Task<WebSearchProviderResult> SearchAsync(
        string query,
        int numResults = 5,
        CancellationToken cancellationToken = default);
}
