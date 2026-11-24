using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Knovara.ControlPlane.Services;

public sealed record IssuedToken(string AccessToken, DateTime ExpiresAt);

/// <summary>
/// Mints the short-lived JWTs that the data-plane API accepts. The tenant claim is what the API's
/// TenantAuthMiddleware trusts, so it is only ever set from the device's server-side record, never from the request.
/// </summary>
public sealed class DeviceTokenIssuer(TokenOptions options, TimeProvider clock)
{
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.SigningKey));
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(string deviceId, string tenantId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.LifetimeMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, deviceId),
                new Claim("did", deviceId),
                new Claim(options.TenantClaim, tenantId)
            ]),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
