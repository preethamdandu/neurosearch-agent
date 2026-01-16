using System.Text.RegularExpressions;

namespace NeuroSearch.Core;

/// <summary>
/// OWASP-compliant input validation and sanitization
/// </summary>
public static class InputValidator
{
    private const int MaxQueryLength = 500;
    private const int MaxUrlLength = 2048;
    
    // Regex patterns for validation
    private static readonly Regex UrlPattern = new(
        @"^https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex DangerousCharsPattern = new(
        @"[<>\""`';()&]",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates and sanitizes search query input
    /// </summary>
    public static ValidationResult ValidateSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ValidationResult.Fail("Search query cannot be empty");

        if (query.Length > MaxQueryLength)
            return ValidationResult.Fail($"Query exceeds maximum length of {MaxQueryLength} characters");

        // Check for potentially malicious patterns
        if (ContainsSqlInjection(query))
            return ValidationResult.Fail("Query contains potentially malicious SQL patterns");

        if (ContainsXssPatterns(query))
            return ValidationResult.Fail("Query contains potentially malicious script patterns");

        // Sanitize the query
        var sanitized = SanitizeText(query);
        return ValidationResult.Success(sanitized);
    }

    /// <summary>
    /// Validates URL input for scraping
    /// </summary>
    public static ValidationResult ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ValidationResult.Fail("URL cannot be empty");

        if (url.Length > MaxUrlLength)
            return ValidationResult.Fail($"URL exceeds maximum length of {MaxUrlLength} characters");

        // Must be HTTP or HTTPS only
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail("URL must start with http:// or https://");

        // Block localhost and internal IPs (SSRF protection)
        if (IsInternalUrl(url))
            return ValidationResult.Fail("Cannot scrape internal/localhost URLs (SSRF protection)");

        // Validate URL format
        if (!UrlPattern.IsMatch(url))
            return ValidationResult.Fail("Invalid URL format");

        return ValidationResult.Success(url);
    }

    /// <summary>
    /// Validates numeric input ranges
    /// </summary>
    public static ValidationResult ValidateNumericRange(int value, int min, int max, string fieldName)
    {
        if (value < min || value > max)
            return ValidationResult.Fail($"{fieldName} must be between {min} and {max}");

        return ValidationResult.Success(value.ToString());
    }

    /// <summary>
    /// Sanitizes text by removing/escaping dangerous characters
    /// </summary>
    private static string SanitizeText(string input)
    {
        // Remove null bytes FIRST (critical for security)
        input = input.Replace("\0", "");

        // Normalize whitespace
        input = Regex.Replace(input, @"\s+", " ").Trim();

        return input;
    }

    /// <summary>
    /// Checks for SQL injection patterns
    /// </summary>
    private static bool ContainsSqlInjection(string input)
    {
        var sqlPatterns = new[]
        {
            @"\bUNION\b.*\bSELECT\b",
            @"\bDROP\b.*\bTABLE\b",
            @";\s*DROP",
            @"--\s*$",
            @"'\s*OR\s*'.*'='",
            @"1\s*=\s*1"
        };

        return sqlPatterns.Any(pattern =>
            Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Checks for XSS patterns
    /// </summary>
    private static bool ContainsXssPatterns(string input)
    {
        var xssPatterns = new[]
        {
            @"<script[^>]*>.*?</script>",
            @"javascript:",
            @"on\w+\s*=",
            @"<iframe",
            @"eval\("
        };

        return xssPatterns.Any(pattern =>
            Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Checks if URL points to internal/localhost (SSRF protection)
    /// </summary>
    private static bool IsInternalUrl(string url)
    {
        var internalPatterns = new[]
        {
            "localhost",
            "127.0.0.1",
            "0.0.0.0",
            "192.168.",
            "10.",
            "172.16.",
            "172.17.",
            "172.18.",
            "172.19.",
            "172.20.",
            "172.21.",
            "172.22.",
            "172.23.",
            "172.24.",
            "172.25.",
            "172.26.",
            "172.27.",
            "172.28.",
            "172.29.",
            "172.30.",
            "172.31."
        };

        return internalPatterns.Any(pattern =>
            url.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Validation result with success/fail + sanitized value
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; init; }
    public string Value { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;

    public static ValidationResult Success(string value) =>
        new() { IsValid = true, Value = value };

    public static ValidationResult Fail(string error) =>
        new() { IsValid = false, ErrorMessage = error };
}
