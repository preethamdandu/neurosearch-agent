using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using NeuroSearch.Core;

namespace NeuroSearch.Agent;

/// <summary>
/// Enforcement point for OWASP LLM01 (indirect prompt injection).
/// Registered as IAutoFunctionInvocationFilter — constraints apply regardless of
/// what the model decides after reading spotlighted untrusted content.
/// </summary>
public sealed class InjectionPolicyFilter : IAutoFunctionInvocationFilter
{
    private readonly InjectionPolicy _policy;
    private readonly InjectionSessionState _state;
    private readonly ILogger? _logger;

    public InjectionPolicyFilter(InjectionSessionState state, ILogger? logger = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _policy = new InjectionPolicy(state);
        _logger = logger;
    }

    /// <summary>Expose policy for unit tests that drive Evaluate without SK auto-invoke.</summary>
    public InjectionPolicy Policy => _policy;

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context,
        Func<AutoFunctionInvocationContext, Task> next)
    {
        if (!TryAuthorize(context.Function, context.Arguments, out var blockMessage))
        {
            context.Result = new FunctionResult(context.Function, blockMessage);
            return;
        }

        await next(context);
    }

    /// <summary>Authorize a function call; used by the filter and by tests.</summary>
    public bool TryAuthorize(KernelFunction function, KernelArguments? arguments, out string blockMessage)
    {
        var plugin = function.PluginName ?? string.Empty;
        var name = function.Name;
        var args = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (arguments != null)
        {
            foreach (var kv in arguments)
                args[kv.Key] = kv.Value?.ToString();
        }

        return TryAuthorize(plugin, name, args, out blockMessage);
    }

    public bool TryAuthorize(
        string plugin,
        string name,
        IReadOnlyDictionary<string, string?> args,
        out string blockMessage)
    {
        var decision = _policy.Evaluate(plugin, name, args);
        if (decision.Allowed)
        {
            blockMessage = string.Empty;
            return true;
        }

        var origin = _state.UntrustedOrigins.LastOrDefault() ?? "n/a";
        var truncatedArgs = TruncateArgs(args);
        var line =
            $"[InjectionPolicy] BLOCK rule={decision.Rule} function={plugin}.{name} " +
            $"origin_url={origin} args={truncatedArgs} msg={decision.Message}";

        _state.LogBlock(line);
        _logger?.LogWarning("{BlockLine}", line);

        blockMessage = $"Blocked by InjectionPolicy ({decision.Rule}): {decision.Message}";
        return false;
    }

    private static string TruncateArgs(IReadOnlyDictionary<string, string?> args)
    {
        var parts = args.Select(kv =>
        {
            var v = kv.Value ?? "";
            if (v.Length > 80) v = v[..80] + "...";
            return $"{kv.Key}={v}";
        });
        var joined = string.Join(";", parts);
        return joined.Length > 240 ? joined[..240] + "..." : joined;
    }
}
