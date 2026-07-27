using System.Text.RegularExpressions;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Assert no committed file contains a plausible third-party API key.
/// Keys must live in user-secrets / environment only.
/// </summary>
public class NoSecretsInRepoTests
{
    // Serper keys are often 40+ hex; Tavily starts with tvly-; OpenAI sk-; generic long tokens
    private static readonly Regex[] Patterns =
    [
        new(@"tvly-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"SERPER_API_KEY\s*=\s*[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"TAVILY_API_KEY\s*=\s*[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new(@"""ApiKey""\s*:\s*""(?!your_|demo-|placeholder)[A-Za-z0-9_\-]{20,}""", RegexOptions.Compiled)
    ];

    private static readonly string[] SkipDirNames =
    [
        ".git", "bin", "obj", ".vs", "node_modules", ".cursor"
    ];

    [Fact]
    public void Repo_Files_Do_Not_Contain_Plausible_Api_Keys()
    {
        var root = FindRepoRoot();
        Assert.True(Directory.Exists(root));

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (ShouldSkip(rel)) continue;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            // Allow documented placeholders
            if (text.Contains("your_serper_api_key_here", StringComparison.Ordinal) ||
                text.Contains("your_tavily_api_key_here", StringComparison.Ordinal) ||
                text.Contains("demo-key", StringComparison.Ordinal))
            {
                // still scan for REAL-looking keys beyond placeholders
            }

            foreach (var rx in Patterns)
            {
                foreach (Match m in rx.Matches(text))
                {
                    var val = m.Value;
                    if (val.Contains("your_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (val.Contains("demo-key", StringComparison.OrdinalIgnoreCase)) continue;
                    if (val.Contains("placeholder", StringComparison.OrdinalIgnoreCase)) continue;
                    // Fixture / test canaries
                    if (val.Contains("CANARY", StringComparison.OrdinalIgnoreCase)) continue;
                    offenders.Add($"{rel}: {Truncate(val, 40)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Plausible API keys found in repo files:\n" + string.Join("\n", offenders.Take(20)));
    }

    private static bool ShouldSkip(string rel)
    {
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => SkipDirNames.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return true;
        // Never scan local secrets
        if (rel.Equals(".env", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".env.local", StringComparison.OrdinalIgnoreCase)) return true;
        // Binary-ish
        var ext = Path.GetExtension(rel);
        if (ext is ".png" or ".jpg" or ".dll" or ".so" or ".dylib" or ".pdf") return true;
        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NeuroSearch.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "MEASUREMENTS.txt")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
