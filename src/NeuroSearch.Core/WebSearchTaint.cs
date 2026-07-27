namespace NeuroSearch.Core;

/// <summary>
/// Centralized taint marking for ALL search providers.
/// Providers return raw hits; this layer marks Untrusted + spotlight.
/// A future provider cannot skip provenance by forgetting a WrapUntrusted call.
/// </summary>
public static class WebSearchTaint
{
    public static string FormatAndTaint(
        InjectionSessionState session,
        WebSearchProviderResult result,
        int hopDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Hits.Count == 0)
            return "No results found for this query.";

        var parts = new List<string>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            var block =
                $"Result {hit.Rank}:\n" +
                $"Title: {hit.Title}\n" +
                $"Snippet: {hit.Snippet}\n" +
                $"URL: {hit.Url}";
            if (!string.IsNullOrWhiteSpace(hit.RawContent))
            {
                // RawContent is the same threat model as scraped HTML — not safer.
                block += $"\nRawContent:\n{hit.RawContent}";
            }
            if (hopDepth > 0)
                block += $"\n[research_hop={hopDepth}]";
            parts.Add(block);
        }

        var formatted = string.Join("\n\n", parts);
        var origin = $"{result.ProviderName}:search:hop{hopDepth}:{result.Query}";
        var tainted = session.Spotlight.WrapUntrusted(formatted, origin, session.Defenses);
        session.MarkUntrusted(origin);

        // Provider-ranked URLs may be scraped; scraped-page URLs may NOT.
        // Explicit boundary — do not "simplify" into authorizing page-discovered links.
        session.AuthorizeProviderResultUrls(
            result.Hits.Select(h => h.Url),
            result.ProviderName);

        return tainted.Text;
    }
}
