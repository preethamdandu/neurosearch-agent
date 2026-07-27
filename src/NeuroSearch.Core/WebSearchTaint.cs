namespace NeuroSearch.Core;

/// <summary>
/// Centralized taint marking for ALL search providers.
/// Providers return raw hits (and optional synthesized <see cref="WebSearchProviderResult.ProviderAnswer"/>);
/// this layer alone owns Untrusted + spotlight + sanitize + length cap so no provider
/// can forget — including future backends with LLM-synthesized answer fields.
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

        if (result.Hits.Count == 0 && string.IsNullOrWhiteSpace(result.ProviderAnswer))
            return "No results found for this query.";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.ProviderAnswer))
        {
            // Synthesized answers are NOT safer than snippets — they are LLM-composed
            // FROM ranked results and are a stronger injection carrier (look first-party).
            // Same threat model as RawContent / scrape body. Not Tavily-specific.
            parts.Add($"ProviderAnswer:\n{result.ProviderAnswer}");
        }

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

        if (parts.Count == 0)
            return "No results found for this query.";

        var formatted = string.Join("\n\n", parts);
        var origin = $"{result.ProviderName}:search:hop{hopDepth}:{result.Query}";
        // WrapUntrusted: length cap → ContentSanitizer (ZW/bidi/NFKC) → delimiter
        // neutralize → spotlight → ContentProvenance.Untrusted. Applies to ProviderAnswer
        // and hits alike because both are in |formatted|.
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
