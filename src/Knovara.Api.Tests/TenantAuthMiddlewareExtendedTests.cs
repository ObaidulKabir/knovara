using Knovara.Api.Middleware;

namespace Knovara.Api.Tests;

/// <summary>
/// Guards the defaults themselves: a missing or partial "Authentication" section must leave the API in
/// the header-trusting development mode rather than half-enabling authentication, and the key minimum
/// must stay at HS256's hash size, which is what Program.cs checks at startup.
/// </summary>
public class TenantAuthOptionsTests
{
    [Fact]
    public void Defaults_AreDisabledWithNoKey()
    {
        var options = new TenantAuthOptions();

        Assert.False(options.Enabled);
        Assert.Null(options.SigningKey);
        Assert.Null(options.Issuer);
        Assert.Null(options.Audience);
    }

    [Fact]
    public void Defaults_UseTidClaim()
    {
        Assert.Equal("tid", new TenantAuthOptions().TenantClaim);
    }

    [Fact]
    public void MinimumSigningKey_MatchesHs256HashSize()
    {
        Assert.Equal(256 / 8, TenantAuthOptions.MinimumSigningKeyBytes);
    }
}
