using System.Collections.Concurrent;

namespace NeuroSearch.Core;

/// <summary>
/// Per-session taint / budget / allowlist state shared by plugins and the
/// InjectionPolicyFilter. One instance per agent process.
/// </summary>
public sealed class InjectionSessionState
{
    private readonly object _gate = new();
    private string? _lastUserMessage;
    private int _toolCallsThisTurn;
    private int _toolCallsThisSession;
    private bool _hasUntrustedInContext;
    private readonly List<string> _untrustedOrigins = new();
    private readonly HashSet<string> _userAuthorizedUrls = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// URLs returned by a search provider this session. Fetchable via WebScraper.
    /// MUST NOT be populated from scraped page text — only from IWebSearchProvider results.
    /// </summary>
    private readonly HashSet<string> _providerAuthorizedUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trustedSubstrings = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _blockLog = new();
    private int _researchHopDepth;
    private int _reSearchCountThisSession;

    private bool _userRequestedMemorySave;

    public SpotlightFormatter Spotlight { get; }

    /// <summary>
    /// Test/benchmark-only defense gates. Default AllOn.
    /// Never loaded from configuration — set only on objects you construct in tests.
    /// </summary>
    public DefenseSwitches Defenses { get; set; } = DefenseSwitches.AllOn;

    /// <summary>Max automatic tool invocations per user turn.</summary>
    public int MaxToolCallsPerTurn { get; init; } = 8;

    /// <summary>Max automatic tool invocations per process/session.</summary>
    public int MaxToolCallsPerSession { get; init; } = 40;

    /// <summary>Max ResearchDeeper hops per session (re-search multi-hop ceiling).</summary>
    public int MaxResearchHops { get; init; } = 3;

    public int ResearchHopDepth
    {
        get { lock (_gate) return _researchHopDepth; }
    }

    public int ReSearchCountThisSession
    {
        get { lock (_gate) return _reSearchCountThisSession; }
    }

    public InjectionSessionState(SpotlightFormatter? spotlight = null, DefenseSwitches? defenses = null)
    {
        Spotlight = spotlight ?? new SpotlightFormatter();
        if (defenses != null)
            Defenses = defenses;
    }

    public string SessionDelimiterId => Spotlight.SessionId;
    public bool HasUntrustedInContext
    {
        get { lock (_gate) return _hasUntrustedInContext; }
    }

    /// <summary>
    /// True when the latest user message explicitly asked to save/remember something.
    /// Narrows the tainted-sink carve-out: research→save is allowed (tagged untrusted),
    /// page-induced Save without user intent is blocked.
    /// </summary>
    public bool UserRequestedMemorySave
    {
        get { lock (_gate) return _userRequestedMemorySave; }
    }

    public IReadOnlyList<string> UntrustedOrigins
    {
        get { lock (_gate) return _untrustedOrigins.ToList(); }
    }

    public int ToolCallsThisTurn
    {
        get { lock (_gate) return _toolCallsThisTurn; }
    }

    public int ToolCallsThisSession
    {
        get { lock (_gate) return _toolCallsThisSession; }
    }

    public string? LastUserMessage
    {
        get { lock (_gate) return _lastUserMessage; }
    }

    /// <summary>Called when the user submits a new message — resets per-turn budget and extracts authorized URLs.</summary>
    public void BeginUserTurn(string userMessage)
    {
        lock (_gate)
        {
            _lastUserMessage = userMessage;
            _toolCallsThisTurn = 0;
            _userRequestedMemorySave = DetectMemorySaveIntent(userMessage);
            ExtractUrls(userMessage, _userAuthorizedUrls);
            // User text is trusted — keep short substrings for exfil grounding checks
            if (!string.IsNullOrWhiteSpace(userMessage) && userMessage.Length >= 16)
                _trustedSubstrings.Add(userMessage.Length > 200 ? userMessage[..200] : userMessage);
            // Also register long tokens (≥24) so planted canaries / secrets in the
            // user message cannot be smuggled into outbound search queries.
            RegisterLongTrustedTokens(userMessage);
        }
    }

    private void RegisterLongTrustedTokens(string userMessage)
    {
        var start = 0;
        for (var i = 0; i <= userMessage.Length; i++)
        {
            var atEnd = i == userMessage.Length;
            var sep = !atEnd && !char.IsLetterOrDigit(userMessage[i]) && userMessage[i] != '_';
            if (!atEnd && !sep) continue;
            var len = i - start;
            if (len >= 24)
                _trustedSubstrings.Add(userMessage.Substring(start, len));
            start = i + 1;
        }
    }

    /// <summary>
    /// Detect explicit user intent to persist findings. Phrase list is intentional —
    /// this is a product intent detector for the tainted-sink carve-out, not a security
    /// blocklist. Keep narrow; prefer false-negative (block save) over false-positive.
    /// </summary>
    public static bool DetectMemorySaveIntent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        ReadOnlySpan<string> intents =
        [
            "save what you find",
            "save what you learn",
            "save it to memory",
            "save to memory",
            "save this",
            "save that",
            "remember this",
            "remember what",
            "store in memory",
            "store this",
            "memorize",
            "add to memory",
            "write to memory"
        ];

        foreach (var intent in intents)
        {
            if (message.Contains(intent, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Record that untrusted content from a tool entered the context.</summary>
    public void MarkUntrusted(string originUrl)
    {
        lock (_gate)
        {
            _hasUntrustedInContext = true;
            if (!string.IsNullOrWhiteSpace(originUrl) && !_untrustedOrigins.Contains(originUrl))
                _untrustedOrigins.Add(originUrl);
        }
    }

    public bool TryConsumeToolBudget(out string? reason)
    {
        lock (_gate)
        {
            if (_toolCallsThisSession >= MaxToolCallsPerSession)
            {
                reason = $"Session tool-call budget exhausted ({MaxToolCallsPerSession})";
                return false;
            }
            if (_toolCallsThisTurn >= MaxToolCallsPerTurn)
            {
                reason = $"Per-turn tool-call budget exhausted ({MaxToolCallsPerTurn})";
                return false;
            }
            _toolCallsThisTurn++;
            _toolCallsThisSession++;
            reason = null;
            return true;
        }
    }

    public bool IsUrlAuthorized(string url)
    {
        lock (_gate)
        {
            if (_userAuthorizedUrls.Contains(url))
                return true;

            foreach (var allowed in _userAuthorizedUrls)
            {
                if (url.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                    allowed.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!string.IsNullOrEmpty(_lastUserMessage) &&
                _lastUserMessage.Contains(url, StringComparison.OrdinalIgnoreCase))
                return true;

            // Same-origin as a user-authorized URL (asset paths on that host are OK)
            if (Uri.TryCreate(url, UriKind.Absolute, out var candidate))
            {
                foreach (var allowed in _userAuthorizedUrls)
                {
                    if (Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri) &&
                        string.Equals(candidate.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Provider-ranked URLs (from IWebSearchProvider) — EXACT normalized URL only.
            // NEVER host-scoped or prefix-matched: authorizing example.com/article must NOT
            // allow example.com/<exfil-payload> or example.com/other-path.
            var normalized = NormalizeUrl(url);
            if (normalized != null && _providerAuthorizedUrls.Contains(normalized))
                return true;

            return false;
        }
    }

    /// <summary>
    /// Normalize for exact provider-URL comparison: lowercase scheme/host/path,
    /// strip default ports, strip trailing slash (except root). Query and fragment
    /// are preserved so variants do not auto-authorize.
    /// </summary>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https"))
            return null;

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');
        path = path.ToLowerInvariant();

        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        // Query/Fragment kept as-is for exact identity (variants do not auto-authorize)
        return $"{scheme}://{host}{port}{path}{uri.Query}{uri.Fragment}";
    }

    public bool IsTextGroundedInUserMessage(string text)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_lastUserMessage))
                return false;

            var needle = text.Trim();
            if (needle.Length > 80)
                needle = needle[..80];

            return _lastUserMessage.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Record URLs returned by a search provider. Exact-URL authorization only
    /// (normalized). Explicit boundary: only call from <see cref="WebSearchTaint"/>
    /// after a real provider response — never from scraped HTML.
    /// Provider authorization clears "unknown host" for that exact URL; it does NOT
    /// clear context-exfil checks on the URL.
    /// </summary>
    public void AuthorizeProviderResultUrls(IEnumerable<string> urls, string providerName)
    {
        lock (_gate)
        {
            foreach (var url in urls)
            {
                var normalized = NormalizeUrl(url);
                if (normalized != null)
                    _providerAuthorizedUrls.Add(normalized);
            }
        }
    }

    public bool IsProviderAuthorizedUrl(string url)
    {
        lock (_gate)
        {
            var normalized = NormalizeUrl(url);
            return normalized != null && _providerAuthorizedUrls.Contains(normalized);
        }
    }

    /// <summary>Clear session-scoped state (provider allowlist, hops, budgets, taint).</summary>
    public void ClearSession()
    {
        lock (_gate)
        {
            _providerAuthorizedUrls.Clear();
            _userAuthorizedUrls.Clear();
            _trustedSubstrings.Clear();
            _untrustedOrigins.Clear();
            _hasUntrustedInContext = false;
            _lastUserMessage = null;
            _toolCallsThisTurn = 0;
            _toolCallsThisSession = 0;
            _researchHopDepth = 0;
            _reSearchCountThisSession = 0;
            _userRequestedMemorySave = false;
        }
    }

    public bool TryBeginResearchHop(out string? reason)
    {
        lock (_gate)
        {
            if (_reSearchCountThisSession >= MaxResearchHops)
            {
                reason = $"Research hop ceiling reached ({MaxResearchHops}). " +
                         "Ask the user for a specific URL or start a new turn.";
                return false;
            }
            _reSearchCountThisSession++;
            _researchHopDepth = _reSearchCountThisSession;
            reason = null;
            return true;
        }
    }

    public bool LooksLikeContextExfiltration(string argumentBlob)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(argumentBlob))
                return false;

            // Context leak: a non-URL trusted substring appears inside the outbound args
            foreach (var trusted in _trustedSubstrings)
            {
                if (trusted.Length < 24)
                    continue;
                if (trusted.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                    trusted.Contains("https://", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (argumentBlob.Contains(trusted, StringComparison.Ordinal))
                    return true;
            }

            // High-entropy alone is NOT sufficient for URL scrapes (JWTs, UTMs).
            // Search-query exfil uses LooksLikeSearchQueryExfiltration instead.
            return false;
        }
    }

    /// <summary>
    /// Exfil check for outbound SEARCH queries (new sink). Attacker-controlled page
    /// text must not smuggle context / canaries / high-entropy blobs into provider APIs.
    /// </summary>
    public bool LooksLikeSearchQueryExfiltration(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        if (LooksLikeContextExfiltration(query))
            return true;
        return ContainsLongHighEntropyToken(query);
    }

    /// <summary>
    /// Detect long base64-ish tokens that look like smuggled context in a search query.
    /// Structural (length + alphabet), not fixture-string matching.
    /// </summary>
    public static bool ContainsLongHighEntropyToken(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 32)
            return false;

        // Split on whitespace / punctuation and inspect tokens
        var start = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            var atEnd = i == s.Length;
            var sep = !atEnd && !(char.IsLetterOrDigit(s[i]) || s[i] is '+' or '/' or '=' or '-' or '_');
            if (!atEnd && !sep) continue;
            var len = i - start;
            if (len >= 32)
            {
                var token = s.AsSpan(start, len);
                var b64 = 0;
                foreach (var c in token)
                {
                    if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_')
                        b64++;
                }
                if (b64 >= len * 0.95)
                    return true;
            }
            start = i + 1;
        }
        return false;
    }

    public void LogBlock(string line)
    {
        _blockLog.Enqueue(line);
        while (_blockLog.Count > 200 && _blockLog.TryDequeue(out _)) { }
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(line);
        Console.ResetColor();
    }

    public IReadOnlyList<string> DrainBlockLog()
    {
        var list = new List<string>();
        while (_blockLog.TryDequeue(out var line))
            list.Add(line);
        return list;
    }

    public static void ExtractUrls(string text, ISet<string> into)
    {
        if (string.IsNullOrEmpty(text)) return;
        var start = 0;
        while (start < text.Length)
        {
            var http = text.IndexOf("http://", start, StringComparison.OrdinalIgnoreCase);
            var https = text.IndexOf("https://", start, StringComparison.OrdinalIgnoreCase);
            int idx;
            if (http < 0) idx = https;
            else if (https < 0) idx = http;
            else idx = Math.Min(http, https);
            if (idx < 0) break;

            var end = idx;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('>' or ')' or '"' or '\'' or ']'))
                end++;
            var url = text[idx..end].TrimEnd('.', ',', ';');
            if (url.Length > 8)
                into.Add(url);
            start = end;
        }
    }

    private static double EstimateEntropy(string s)
    {
        if (s.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        counts.Clear();
        foreach (var c in s)
            counts[(byte)c]++;
        double entropy = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / s.Length;
            entropy -= p * Math.Log(p, 2);
        }
        return entropy;
    }
}
