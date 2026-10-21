using CentralTickets.Api;

namespace CentralTickets.Api.Tests;

public sealed class LocalAccessTests
{
    [Fact]
    public void Bootstrap_token_is_compared_without_accepting_a_different_value()
    {
        var access = new LocalAccess("token-de-teste");

        Assert.True(access.Matches("token-de-teste"));
        Assert.False(access.Matches("token-de-teste-errado"));
    }

    [Fact]
    public void Session_cookie_is_random_and_validated_separately_from_bootstrap_token()
    {
        var access = new LocalAccess("token-de-teste");
        var otherAccess = new LocalAccess("token-de-teste");

        Assert.True(access.MatchesSession(access.SessionToken));
        Assert.False(access.MatchesSession(otherAccess.SessionToken));
        Assert.False(access.MatchesSession("nao-e-base64!!"));
    }
}
