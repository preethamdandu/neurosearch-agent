using System.Globalization;
using System.Text;

namespace NeuroSearch.Core;

/// <summary>
/// Structural sanitization of fetched web text before it enters the LLM context.
/// Strips zero-width / bidi override characters and normalizes to NFKC to reduce
/// homoglyph and invisible-character obfuscation. Caps length.
/// </summary>
public static class ContentSanitizer
{
    /// <summary>Hard cap on scraped/search body text after sanitization (characters).</summary>
    public const int MaxContentLength = 2500;

    public static string Sanitize(string? text, int maxLength = MaxContentLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (IsInvisibleOrBidiOverride(ch))
                continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength] + "\n\n[Content truncated for length...]";

        return cleaned;
    }

    private static bool IsInvisibleOrBidiOverride(char ch)
    {
        // Zero-width: U+200B..U+200D, BOM U+FEFF
        if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
            return true;

        // Bidi overrides / embeddings: U+202A..U+202E, also U+2066..U+2069
        if (ch is >= '\u202A' and <= '\u202E')
            return true;
        if (ch is >= '\u2066' and <= '\u2069')
            return true;

        // Drop other non-spacing format chars that are commonly used for smuggling
        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category == UnicodeCategory.Format && ch != '\n' && ch != '\r' && ch != '\t';
    }
}
