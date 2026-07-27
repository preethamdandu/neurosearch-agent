using HtmlAgilityPack;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text;
using NeuroSearch.Core;

namespace NeuroSearch.Plugins;

/// <summary>
/// SECURITY-HARDENED Web scraping plugin
/// - URL validation (SSRF protection, no localhost)
/// - Rate limiting (5 req/min to prevent abuse)
/// - Content-boundary: returns Untrusted spotlighted text (OWASP LLM01)
/// </summary>
public class WebScraperPlugin
{
    private readonly HttpClient _httpClient;
    private readonly RateLimiter _rateLimiter;
    private readonly InjectionSessionState _session;
    private const int RequestTimeoutSeconds = 10;

    public WebScraperPlugin(HttpClient httpClient, InjectionSessionState? session = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _session = session ?? new InjectionSessionState();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (compatible; NeuroSearchBot/1.0)");
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);

        _rateLimiter = new RateLimiter(requestsPerMinute: 5, burstSize: 3);
    }

    /// <summary>Expose session for tests / filter wiring.</summary>
    public InjectionSessionState Session => _session;

    [KernelFunction]
    [Description("Extracts main text content from a web page URL. Use this to read full articles, blog posts, or documentation pages.")]
    public async Task<string> ScrapeUrlAsync(
        [Description("The URL to scrape (must be a valid HTTP/HTTPS URL)")]
        string url,
        CancellationToken cancellationToken = default)
    {
        var validationResult = InputValidator.ValidateUrl(url);
        if (!validationResult.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[❌ ScraperPlugin] Validation failed: {validationResult.ErrorMessage}");
            Console.ResetColor();
            return $"Error: {validationResult.ErrorMessage}";
        }

        var validatedUrl = validationResult.Value!;

        var rateLimitResult = _rateLimiter.AllowRequest("scraper_api");
        if (!rateLimitResult.IsAllowed)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[⚠️  ScraperPlugin] Rate limited. Retry after {rateLimitResult.RetryAfterSeconds}s");
            Console.ResetColor();
            return $"Error: Rate limit exceeded. Please retry after {rateLimitResult.RetryAfterSeconds} seconds (HTTP 429).";
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[📄 ScraperPlugin] Scraping URL: {validatedUrl}");
        Console.ResetColor();

        try
        {
            var html = await _httpClient.GetStringAsync(validatedUrl, cancellationToken);
            var extracted = ExtractText(html);

            // Content-boundary defense: sanitize + spotlight as Untrusted
            var tainted = _session.Spotlight.WrapUntrusted(extracted, validatedUrl);
            _session.MarkUntrusted(validatedUrl);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓ ScraperPlugin] Extracted {extracted.Length} chars → spotlighted Untrusted (id={_session.SessionDelimiterId})");
            Console.ResetColor();

            return tainted.Text;
        }
        catch (HttpRequestException ex)
        {
            return $"Failed to fetch URL: {ex.Message}. The page may be blocked or unavailable.";
        }
        catch (Exception ex)
        {
            return $"Error scraping URL: {ex.Message}";
        }
    }

    /// <summary>Extract readable text from HTML (shared with tests).</summary>
    internal static string ExtractText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var nodesToRemove = doc.DocumentNode.Descendants()
            .Where(n => n.Name == "script" ||
                       n.Name == "style" ||
                       n.Name == "nav" ||
                       n.Name == "footer" ||
                       n.Name == "header" && !n.HasClass("article-header"))
            .ToList();

        foreach (var node in nodesToRemove)
            node.Remove();

        var contentNode = doc.DocumentNode.SelectSingleNode("//article") ??
                        doc.DocumentNode.SelectSingleNode("//main") ??
                        doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'content')]") ??
                        doc.DocumentNode;

        var textNodes = contentNode.SelectNodes(".//p | .//h1 | .//h2 | .//h3 | .//li");

        if (textNodes == null || textNodes.Count == 0)
            return "Could not extract meaningful content from this URL. The page may require JavaScript or use an unsupported format.";

        var content = new StringBuilder();
        foreach (var node in textNodes)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 10)
            {
                content.AppendLine(text);
                content.AppendLine();
            }

            if (content.Length > ContentSanitizer.MaxContentLength)
                break;
        }

        var result = content.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            return "No readable content found on this page.";

        return result;
    }
}
