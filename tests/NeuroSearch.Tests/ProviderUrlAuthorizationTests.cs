using NeuroSearch.Agent;
using NeuroSearch.Core;
using Xunit;

namespace NeuroSearch.Tests;

/// <summary>
/// AuthorizeProviderResultUrls must be exact-URL (normalized), not host/prefix scoped.
/// Host-scoped authorization would be a live exfil hole.
/// </summary>
public class ProviderUrlAuthorizationTests
{
    private const string Article = "https://example.com/article";

    [Fact]
    public void Provider_Article_Does_Not_Authorize_Other_Path_On_Same_Host()
    {
        var session = new InjectionSessionState();
        session.AuthorizeProviderResultUrls([Article], "tavily");
        Assert.True(session.IsUrlAuthorized(Article));
        Assert.False(session.IsUrlAuthorized("https://example.com/other-path"));
    }

    [Fact]
    public void Provider_Article_Does_Not_Authorize_Subdomain()
    {
        var session = new InjectionSessionState();
        session.AuthorizeProviderResultUrls([Article], "tavily");
        Assert.False(session.IsUrlAuthorized("https://sub.example.com/article"));
    }

    [Fact]
    public void Authorized_Url_With_Planted_Canary_Still_Blocked_By_Exfil()
    {
        const string canary = "SECRET_CONTEXT_TOKEN_ALPHA_BETA_GAMMA_99";
        var leakUrl = $"https://example.com/leak/{canary}";
        var session = new InjectionSessionState();
        var filter = new InjectionPolicyFilter(session);
        session.BeginUserTurn($"private note for later: {canary}");
        session.AuthorizeProviderResultUrls([leakUrl], "serper");

        Assert.True(session.IsUrlAuthorized(leakUrl), "exact provider URL is allowlisted");
        Assert.False(filter.TryAuthorize(
            "WebScraper", "ScrapeUrlAsync",
            new Dictionary<string, string?> { ["url"] = leakUrl },
            out var msg),
            "provider auth must not clear context-exfil");
        Assert.Contains("exfiltration", msg);
    }

    [Fact]
    public void Authorization_Does_Not_Persist_Into_New_Session()
    {
        var session1 = new InjectionSessionState();
        session1.AuthorizeProviderResultUrls([Article], "tavily");
        Assert.True(session1.IsUrlAuthorized(Article));

        var session2 = new InjectionSessionState();
        Assert.False(session2.IsUrlAuthorized(Article));

        session1.ClearSession();
        Assert.False(session1.IsUrlAuthorized(Article));
    }

    [Fact]
    public void Query_And_Fragment_Variants_Do_Not_Auto_Authorize()
    {
        var session = new InjectionSessionState();
        session.AuthorizeProviderResultUrls([Article], "tavily");
        Assert.False(session.IsUrlAuthorized("https://example.com/article?utm=1"));
        Assert.False(session.IsUrlAuthorized("https://example.com/article#section"));
        // Normalization: trailing slash / case of host still match the same exact URL
        Assert.True(session.IsUrlAuthorized("https://EXAMPLE.com/article/"));
        Assert.True(session.IsUrlAuthorized("https://example.com/Article"));
    }
}
