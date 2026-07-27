using System.Text.RegularExpressions;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// Assert no committed file contains a plausible third-party API key.
/// Keys must live in user-secrets / environment only.
/// </summary>
public class NoSecretsInRepoTests
{
    // Explicit provider prefixes + assignment forms. Placeholders (your_*, demo-key, tvly-...) allowed.
    private static readonly Regex[] Patterns =
    [
        new(@"tvly-(?![\.]{2,})[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"SERPER_API_KEY\s*=\s*(?!your_|demo-)[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"TAVILY_API_KEY\s*=\s*(?!your_|demo-)[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),
        new(@"Serper:ApiKey[""'\s:=]+(?!your_|demo-|\.\.\.)[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"Tavily:ApiKey[""'\s:=]+(?!your_|demo-|\.\.\.)[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),
        new(@"""ApiKey""\s*:\s*""(?!your_|demo-|placeholder|\.\.\.)[A-Za-z0-9_\-]{20,}""", RegexOptions.Compiled),
        // Serper-shaped: only flag when assignment form looks like a real key value
        new(@"(?i)Serper:ApiKey[""'\s:=]+(?!your_|demo-|\.\.\.)([0-9a-f]{40})", RegexOptions.Compiled)
    ];

    private static readonly string[] SkipDirNames =
    [
        ".git", "bin", "obj", ".vs", "node_modules", ".cursor", "artifacts"
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
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (var rx in Patterns)
            {
                foreach (Match m in rx.Matches(text))
                {
                    var val = m.Value;
                    if (IsAllowedPlaceholder(val)) continue;
                    offenders.Add($"{rel}: {Truncate(val, 48)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Plausible API keys found in repo files:\n" + string.Join("\n", offenders.Take(20)));
    }

    [Fact]
    public void DotEnv_Is_Gitignored_And_Not_Tracked()
    {
        var root = FindRepoRoot();
        var gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains(".env", gitignore);

        // git ls-files --error-unmatch .env must fail (not tracked)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-files --error-unmatch .env",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10_000);
        Assert.NotEqual(0, p.ExitCode);
    }

    [Fact]
    public void Dockerfile_Does_Not_Copy_Env_Or_Embed_Keys()
    {
        var root = FindRepoRoot();
        foreach (var name in new[] { "Dockerfile", "Dockerfile.aot", "docker-compose.yml", "docker-compose.yaml" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("COPY .env", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ADD .env", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tvly-", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"SERPER_API_KEY\s*=\s*[A-Za-z0-9]{20,}"), text);
        }
    }

    [Fact]
    public void Committed_Search_Fixtures_Are_Redacted()
    {
        var root = FindRepoRoot();
        var fixtures = Path.Combine(root, "tests", "NeuroSearch.Tests", "Fixtures");
        if (!Directory.Exists(fixtures)) return;
        foreach (var file in Directory.EnumerateFiles(fixtures, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("tvly-", text);
            Assert.DoesNotMatch(new Regex(@"""api_key""\s*:\s*""[^""]{8,}""", RegexOptions.IgnoreCase), text);
            Assert.DoesNotMatch(new Regex(@"SERPER_API_KEY\s*=\s*[A-Za-z0-9]{20,}"), text);
        }
    }

    private static bool IsAllowedPlaceholder(string val) =>
        val.Contains("your_", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("demo-key", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("CANARY", StringComparison.OrdinalIgnoreCase) ||
        val.Contains("...", StringComparison.Ordinal) ||
        // README docs: tvly-... truncated form
        Regex.IsMatch(val, @"^tvly-\.+$");

    private static bool ShouldSkip(string rel)
    {
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(p => SkipDirNames.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return true;
        if (rel.Equals(".env", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".env.local", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Contains(".dSYM", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(rel);
        if (ext is ".png" or ".jpg" or ".dll" or ".so" or ".dylib" or ".pdf" or ".o") return true;
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
