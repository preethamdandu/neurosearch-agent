namespace NeuroSearch.Core;

/// <summary>
/// Provenance of content that may enter the LLM context or vector memory.
/// Trusted = user input / system config. Untrusted = tool-fetched web content.
/// </summary>
public enum ContentProvenance
{
    Trusted = 0,
    Untrusted = 1
}

/// <summary>
/// Content carrying an explicit trust tag. Untrusted text must be spotlighted
/// before it enters chat history and must retain provenance if saved to memory.
/// </summary>
public sealed record TaintedContent(
    string Text,
    ContentProvenance Source,
    string OriginUrl)
{
    public bool IsUntrusted => Source == ContentProvenance.Untrusted;
}
