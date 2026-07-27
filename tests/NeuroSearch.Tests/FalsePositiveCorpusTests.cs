using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using NeuroSearch.Agent;
using NeuroSearch.Core;
using NeuroSearch.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace NeuroSearch.Tests;

/// <summary>
/// False-positive rate of the exfil *heuristic* against a committed corpus.
/// Unauthorized-URL blocks are expected (user didn't ask for that host) and are
/// counted separately — they are not heuristic false positives.
/// </summary>
public class FalsePositiveCorpusTests
{
    private readonly ITestOutputHelper _out;
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FalsePositiveCorpusTests(ITestOutputHelper output) => _out = output;

    private static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "tests", "NeuroSearch.Tests", "Fixtures", "BenignPages"),
                         Path.Combine(dir.FullName, "Fixtures", "BenignPages")
                     })
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Fixtures/BenignPages not found");
    }

    private sealed class FixtureHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task Benign_Corpus_Exfil_Heuristic_False_Positive_Rate()
    {
        var dir = FixturesDir();
        var files = Directory.GetFiles(dir, "*.html").OrderBy(f => f).ToArray();
        Assert.True(files.Length >= 30, $"Need ≥30 fixtures, found {files.Length}");

        var heuristicFpPages = 0;
        var unauthorizedOnlyPages = 0;
        var heuristicFpBlocks = 0;
        var unauthorizedBlocks = 0;
        var outboundAttempts = 0;
        var causes = new List<string>();

        foreach (var file in files)
        {
            var html = await File.ReadAllTextAsync(file);
            var name = Path.GetFileName(file);
            var scrapeUrl = "https://corpus.test/article";

            var session = new InjectionSessionState(new SpotlightFormatter(
                Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()))
            {
                MaxToolCallsPerTurn = int.MaxValue,
                MaxToolCallsPerSession = int.MaxValue
            };
            session.BeginUserTurn($"summarize {scrapeUrl}");

            var scraper = new WebScraperPlugin(new HttpClient(new FixtureHandler(html)), session);
            var scraped = await scraper.ScrapeUrlAsync(scrapeUrl);

            var urls = UrlRegex.Matches(scraped)
                .Select(m => m.Value.TrimEnd('.', ',', ')', ';', ']'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Take(25)
                .ToList();

            var pageHadHeuristicFp = false;
            var pageHadUnauthorized = false;

            foreach (var url in urls)
            {
                outboundAttempts++;

                // Pass 1: only page URL authorized → expect unauthorized for foreign hosts
                var filter1 = new InjectionPolicyFilter(session);
                var ok1 = filter1.TryAuthorize(
                    "WebScraper", "ScrapeUrlAsync",
                    new Dictionary<string, string?> { ["url"] = url },
                    out var msg1);

                if (!ok1 && msg1.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    unauthorizedBlocks++;
                    pageHadUnauthorized = true;
                }

                // Pass 2: authorize this URL in the user turn, then re-check.
                // If STILL blocked → exfil heuristic false positive.
                var session2 = new InjectionSessionState(new SpotlightFormatter("fp2"), session.Defenses)
                {
                    MaxToolCallsPerTurn = int.MaxValue,
                    MaxToolCallsPerSession = int.MaxValue
                };
                session2.BeginUserTurn($"summarize {scrapeUrl} and also fetch {url}");
                session2.MarkUntrusted(scrapeUrl);
                var filter2 = new InjectionPolicyFilter(session2);
                var ok2 = filter2.TryAuthorize(
                    "WebScraper", "ScrapeUrlAsync",
                    new Dictionary<string, string?> { ["url"] = url },
                    out var msg2);

                if (!ok2 && msg2.Contains("exfiltration", StringComparison.OrdinalIgnoreCase))
                {
                    heuristicFpBlocks++;
                    pageHadHeuristicFp = true;
                    causes.Add($"{name}: HEURISTIC_FP url={Truncate(url, 90)}");
                }
                else if (!ok2)
                {
                    causes.Add($"{name}: OTHER_BLOCK url={Truncate(url, 90)} msg={Truncate(msg2, 60)}");
                }
            }

            if (pageHadHeuristicFp) heuristicFpPages++;
            else if (pageHadUnauthorized) unauthorizedOnlyPages++;
        }

        _out.WriteLine($"CORPUS_PAGES={files.Length}");
        _out.WriteLine($"HEURISTIC_FP_PAGES={heuristicFpPages}");
        _out.WriteLine($"UNAUTHORIZED_ONLY_PAGES={unauthorizedOnlyPages}");
        _out.WriteLine($"OUTBOUND_ATTEMPTS={outboundAttempts}");
        _out.WriteLine($"HEURISTIC_FP_BLOCKS={heuristicFpBlocks}");
        _out.WriteLine($"UNAUTHORIZED_BLOCKS={unauthorizedBlocks}");
        foreach (var c in causes.Take(50))
            _out.WriteLine(c);

        var reportPath = Path.Combine(dir, "..", "fp-report.txt");
        await File.WriteAllTextAsync(reportPath,
            $"CORPUS_PAGES={files.Length}\n" +
            $"HEURISTIC_FP_PAGES={heuristicFpPages}\n" +
            $"UNAUTHORIZED_ONLY_PAGES={unauthorizedOnlyPages}\n" +
            $"OUTBOUND_ATTEMPTS={outboundAttempts}\n" +
            $"HEURISTIC_FP_BLOCKS={heuristicFpBlocks}\n" +
            $"UNAUTHORIZED_BLOCKS={unauthorizedBlocks}\n" +
            string.Join('\n', causes) + "\n");

        // Heuristic must not flag a material share of authorized benign URLs.
        // Threshold: fewer than half the pages (raw count, not a precision claim).
        Assert.True(heuristicFpPages * 2 < files.Length,
            $"Heuristic FP too high: {heuristicFpPages}/{files.Length} pages — see fp-report.txt");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
