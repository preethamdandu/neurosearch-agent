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

        // 1. Allowlist (accept Foo / FooAsync variants — models often drop the Async suffix)
        if (_state.Defenses.Allowlist && !IsAllowlisted(pluginName, functionName))
        {
            return PolicyDecision.Block(
                "allowlist",
                $"Blocked unknown function '{fq}' — not on the tool allowlist.");
        }

        // 2. Budget (consume on evaluate so runaway chains trip the gate)
        if (_state.Defenses.Budget && !_state.TryConsumeToolBudget(out var budgetReason))
        {
            return PolicyDecision.Block("budget", budgetReason!);
        }

        // 3. Tainted-sink: VectorMemory.Save while untrusted content is in context.
        // Carve-out (narrowed): allow when the USER explicitly requested a memory save
        // this turn (research→save happy path) OR the text is grounded in the user message.
        // Payload is still tagged provenance=untrusted by VectorMemoryPlugin.
        // Page-induced Save without user save-intent remains blocked (poisoning defense).
        if (_state.Defenses.TaintedSinkRule &&
            IsSave(pluginName, functionName) &&
            _state.HasUntrustedInContext)
        {
            var text = GetArg(args, "text") ?? string.Empty;
            var userAskedToSave = _state.UserRequestedMemorySave;
            var grounded = _state.IsTextGroundedInUserMessage(text);
            if (!userAskedToSave && !grounded)
            {
                return PolicyDecision.Block(
                    "tainted-sink",
                    "Blocked VectorMemory.Save: untrusted web content is in context and " +
                    "the user did not request a memory save this turn " +
                    "(memory-poisoning defense).");
            }
        }

        // 4. Exfiltration / unauthorized outbound URL
        if (_state.Defenses.ExfilCheck && IsOutboundNetwork(pluginName, functionName))
        {
            var url = GetArg(args, "url") ?? GetArg(args, "query") ?? string.Empty;
            var argBlob = string.Join("&", args.Select(kv => $"{kv.Key}={kv.Value}"));

            if (IsScraper(pluginName, functionName))
            {
                if (!_state.IsUrlAuthorized(url))
                {
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

            // Whole-URL exfil shapes (not querystring-only): path, subdomain, fragment
            if (LooksLikeExfilUrlShape(url))
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

    private static bool IsAllowlisted(string plugin, string function)
    {
        var fq = $"{plugin}.{function}";
        var fqDash = $"{plugin}-{function}";
        if (AllowedFunctions.Contains(fq) || AllowedFunctions.Contains(fqDash))
            return true;

        // Models / SK sometimes omit the Async suffix
        if (!function.EndsWith("Async", StringComparison.OrdinalIgnoreCase))
        {
            var withAsync = function + "Async";
            return AllowedFunctions.Contains($"{plugin}.{withAsync}") ||
                   AllowedFunctions.Contains($"{plugin}-{withAsync}");
        }

        return false;
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

    /// <summary>
    /// Detect exfil URL shapes across query, path, subdomain, and fragment.
    /// Intentionally narrow on non-attacker hosts to avoid CDN/UTM false positives;
    /// attacker.* canaries and context-substring checks carry the rest.
    /// </summary>
    internal static bool LooksLikeExfilUrlShape(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Contains("attacker.", StringComparison.OrdinalIgnoreCase) &&
                   (url.Contains('?', StringComparison.Ordinal) ||
                    url.Contains('#', StringComparison.Ordinal) ||
                    HasLongBase64ishToken(url));
        }

        var host = uri.Host ?? "";
        var path = uri.AbsolutePath ?? "";
        var query = uri.Query ?? "";
        var fragment = uri.Fragment ?? "";

        var isAttackerHost = host.Contains("attacker.", StringComparison.OrdinalIgnoreCase) ||
                             host.EndsWith(".attacker.test", StringComparison.OrdinalIgnoreCase) ||
                             host.Equals("attacker.test", StringComparison.OrdinalIgnoreCase);

        if (!isAttackerHost)
            return false; // non-attacker hosts: rely on IsUrlAuthorized + LooksLikeContextExfiltration

        // attacker host: any query, fragment, long path token, or base64 subdomain label
        if (!string.IsNullOrEmpty(query) && query.Length > 1)
            return true;
        if (!string.IsNullOrEmpty(fragment) && fragment.Length > 1)
            return true;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Length >= 8 && HasLongBase64ishToken(s)))
            return true;
        if (path.Length > 8 && segments.Length > 0)
            return true;

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length >= 3 && HasLongBase64ishToken(labels[0]))
            return true;

        return false;
    }

    private static bool HasLongBase64ishToken(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 12)
            return false;
        var b64Chars = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_')
                b64Chars++;
        }
        return b64Chars >= s.Length * 0.9 && s.Length >= 12;
    }
}
