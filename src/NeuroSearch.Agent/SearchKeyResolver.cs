using Microsoft.Extensions.Configuration;
using NeuroSearch.Core;

namespace NeuroSearch.Agent;

/// <summary>
/// Resolves search API keys from user-secrets / environment only — never from appsettings.json values
/// that would embed secrets in the repo. appsettings may name the provider; keys come from secrets/env.
/// </summary>
public static class SearchKeyResolver
{
    public static string ResolveProviderName(IConfiguration config, string? cliOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
            return cliOverride.Trim().ToLowerInvariant();
        var fromConfig = config["Search:Provider"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig.Trim().ToLowerInvariant();
        return "tavily";
    }

    public static string? ResolveSerperKey(IConfiguration config)
    {
        return FirstNonEmpty(
            config["Serper:ApiKey"],
            Environment.GetEnvironmentVariable("SERPER_API_KEY"),
            Environment.GetEnvironmentVariable("Serper__ApiKey"));
    }

    public static string? ResolveTavilyKey(IConfiguration config)
    {
        return FirstNonEmpty(
            config["Tavily:ApiKey"],
            Environment.GetEnvironmentVariable("TAVILY_API_KEY"),
            Environment.GetEnvironmentVariable("Tavily__ApiKey"));
    }

    public static bool IsUsableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        !key.Equals("demo-key", StringComparison.OrdinalIgnoreCase) &&
        !key.Contains("your_", StringComparison.OrdinalIgnoreCase) &&
        !key.Contains("placeholder", StringComparison.OrdinalIgnoreCase) &&
        key.Length >= 16;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}
