namespace NeuroSearch.Core;

/// <summary>
/// Decision from the injection policy filter. Structural enforcement — not a regex blocklist.
/// </summary>
public sealed record PolicyDecision(
    bool Allowed,
    string Rule,
    string Message)
{
    public static PolicyDecision Allow() => new(true, "allow", "ok");
    public static PolicyDecision Block(string rule, string message) => new(false, rule, message);
}

/// <summary>
/// Pure policy evaluator for tool-call gating (OWASP LLM01 defense-in-depth).
/// Order: allowlist → budget → tainted-sink → exfiltration.
/// A secondary phrase-signal logger may fire but NEVER enforces.
/// </summary>
public sealed class InjectionPolicy
{
    public static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "WebSearch.SearchAsync",
        "WebScraper.ScrapeUrlAsync",
        "VectorMemory.SaveToMemoryAsync",
        "VectorMemory.SearchMemoryAsync",
        "VectorMemory.GetMemoryStatsAsync",
        // SK may report plugin-function with hyphen
        "WebSearch-SearchAsync",
        "WebScraper-ScrapeUrlAsync",
        "VectorMemory-SaveToMemoryAsync",
        "VectorMemory-SearchMemoryAsync",
        "VectorMemory-GetMemoryStatsAsync"
    };

    private readonly InjectionSessionState _state;

    public InjectionPolicy(InjectionSessionState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PolicyDecision Evaluate(string pluginName, string functionName, IReadOnlyDictionary<string, string?> args)
    {
        var fq = $"{pluginName}.{functionName}";
        var fqAlt = $"{pluginName}-{functionName}";

        // 1. Allowlist
        if (!AllowedFunctions.Contains(fq) && !AllowedFunctions.Contains(fqAlt))
        {
            return PolicyDecision.Block(
                "allowlist",
                $"Blocked unknown function '{fq}' — not on the tool allowlist.");
        }

        // 2. Budget (consume on evaluate so runaway chains trip the gate)
        if (!_state.TryConsumeToolBudget(out var budgetReason))
        {
            return PolicyDecision.Block("budget", budgetReason!);
        }

        // 3. Tainted-sink: VectorMemory.Save while untrusted content is in context
        if (IsSave(pluginName, functionName) && _state.HasUntrustedInContext)
        {
            var text = GetArg(args, "text") ?? string.Empty;
            if (!_state.IsTextGroundedInUserMessage(text))
            {
                return PolicyDecision.Block(
                    "tainted-sink",
                    "Blocked VectorMemory.Save: untrusted web content is in context and " +
                    "save text is not grounded in the latest user message (memory-poisoning defense).");
            }
        }

        // 4. Exfiltration / unauthorized outbound URL
        if (IsOutboundNetwork(pluginName, functionName))
        {
            var url = GetArg(args, "url") ?? GetArg(args, "query") ?? string.Empty;
            var argBlob = string.Join("&", args.Select(kv => $"{kv.Key}={kv.Value}"));

            if (IsScraper(pluginName, functionName))
            {
                if (!_state.IsUrlAuthorized(url))
                {
                    // Secondary signal only — still block on authorization failure
                    LogSecondarySignalIfPresent(url);
                    return PolicyDecision.Block(
                        "exfiltration",
                        $"Blocked WebScraper call to unauthorized URL '{Truncate(url, 120)}'. " +
                        "URL must appear in the user's request.");
                }
            }

            if (_state.LooksLikeContextExfiltration(argBlob) ||
                _state.LooksLikeContextExfiltration(url))
            {
                return PolicyDecision.Block(
                    "exfiltration",
                    "Blocked outbound call: arguments look like context exfiltration " +
                    "(high-entropy query string or trusted-context substring in URL).");
            }

            // Markdown-image style exfil host patterns in URL query
            if (url.Contains("attacker.", StringComparison.OrdinalIgnoreCase) &&
                url.Contains('?', StringComparison.Ordinal))
            {
                return PolicyDecision.Block(
                    "exfiltration",
                    "Blocked outbound call to suspected exfiltration endpoint.");
            }
        }

        // Secondary signal (logged only — NEVER the enforcement point)
        foreach (var value in args.Values)
            LogSecondarySignalIfPresent(value);

        return PolicyDecision.Allow();
    }

    private void LogSecondarySignalIfPresent(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Soft signals for operators — not used to allow/deny
        ReadOnlySpan<string> signals =
        [
            "ignore previous instructions",
            "disregard your instructions",
            "you are now",
            "decode and execute"
        ];
        foreach (var s in signals)
        {
            if (text.Contains(s, StringComparison.OrdinalIgnoreCase))
            {
                _state.LogBlock(
                    $"[InjectionPolicy] SECONDARY_SIGNAL rule=phrase-hint phrase=\"{s}\" " +
                    $"(logged only — not an enforcement point)");
                break;
            }
        }
    }

    private static bool IsSave(string plugin, string fn) =>
        plugin.Equals("VectorMemory", StringComparison.OrdinalIgnoreCase) &&
        fn.Contains("Save", StringComparison.OrdinalIgnoreCase);

    private static bool IsScraper(string plugin, string fn) =>
        plugin.Equals("WebScraper", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutboundNetwork(string plugin, string fn) =>
        plugin.Equals("WebScraper", StringComparison.OrdinalIgnoreCase) ||
        plugin.Equals("WebSearch", StringComparison.OrdinalIgnoreCase);

    private static string? GetArg(IReadOnlyDictionary<string, string?> args, string name)
    {
        foreach (var kv in args)
        {
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
