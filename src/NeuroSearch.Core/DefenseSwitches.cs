namespace NeuroSearch.Core;

/// <summary>
/// Test/benchmark-only gates for mutation testing of LLM01 defenses.
/// Defaults to all-on. MUST NOT be loaded from env vars, appsettings, or CLI —
/// shipping a runtime kill switch for security controls is forbidden.
/// Set only by constructing InjectionSessionState in test/benchmark code.
///
/// Enforcing (6): ContentSanitizer, DelimiterNeutralizer, SpotlightWrapper,
/// TaintedSinkRule, Allowlist (tools + outbound hosts/provenance), Budget.
/// Advisory only: ExfilCheck — toggles shape/entropy LOG volume; never denies.
/// </summary>
public sealed record DefenseSwitches(
    bool ContentSanitizer = true,
    bool DelimiterNeutralizer = true,
    bool SpotlightWrapper = true,
    bool TaintedSinkRule = true,
    /// <summary>ADVISORY: log exfil-shape signals. Non-enforcing.</summary>
    bool ExfilCheck = true,
    bool Allowlist = true,
    bool Budget = true)
{
    public static DefenseSwitches AllOn { get; } = new();

    public static DefenseSwitches AllOff { get; } = new(
        ContentSanitizer: false,
        DelimiterNeutralizer: false,
        SpotlightWrapper: false,
        TaintedSinkRule: false,
        ExfilCheck: false,
        Allowlist: false,
        Budget: false);

    public DefenseSwitches With(
        bool? contentSanitizer = null,
        bool? delimiterNeutralizer = null,
        bool? spotlightWrapper = null,
        bool? taintedSinkRule = null,
        bool? exfilCheck = null,
        bool? allowlist = null,
        bool? budget = null) => new(
            contentSanitizer ?? ContentSanitizer,
            delimiterNeutralizer ?? DelimiterNeutralizer,
            spotlightWrapper ?? SpotlightWrapper,
            taintedSinkRule ?? TaintedSinkRule,
            exfilCheck ?? ExfilCheck,
            allowlist ?? Allowlist,
            budget ?? Budget);
}
