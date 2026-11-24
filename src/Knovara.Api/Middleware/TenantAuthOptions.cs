namespace Knovara.Api.Middleware;

/// <summary>Bound from the "Authentication" configuration section. See README.md "Authentication".</summary>
public sealed class TenantAuthOptions
{
    /// <summary>HS256 rejects keys shorter than its own hash size, so a shorter key fails at validation time, not startup.</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>
    /// False (the default) trusts the X-Tenant-Id header, which is only safe on a developer machine or
    /// behind a trusted gateway. True requires a valid token on every request and ignores the header.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Symmetric signing key. Supply it from configuration or a secret store; never commit one.</summary>
    public string? SigningKey { get; init; }

    /// <summary>Validated only when set, so a single-tenant dev issuer needs no extra configuration.</summary>
    public string? Issuer { get; init; }
    public string? Audience { get; init; }

    public string TenantClaim { get; init; } = "tid";
}
