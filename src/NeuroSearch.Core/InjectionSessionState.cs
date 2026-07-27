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
    private readonly HashSet<string> _trustedSubstrings = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _blockLog = new();

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

            return false;
        }
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
                // User messages often contain the authorized URL itself — that is NOT exfil
                if (trusted.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                    trusted.Contains("https://", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (argumentBlob.Contains(trusted, StringComparison.Ordinal))
                    return true;
            }

            // High-entropy query alone is common (JWT, UTM, signed CDN) — not sufficient.
            // Only treat as exfil when the blob also embeds a sensitive context marker.
            return false;
        }
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
