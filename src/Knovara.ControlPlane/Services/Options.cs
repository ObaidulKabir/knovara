using System.Text;

namespace Knovara.ControlPlane.Services;

/// <summary>
/// How device access tokens are signed. The data-plane API validates them with Authentication:SigningKey,
/// Issuer and Audience, so these three must be identical on both sides. A 32-byte minimum is enforced at startup.
/// </summary>
public sealed class TokenOptions
{
    public const int MinimumSigningKeyBytes = 32;

    public string SigningKey { get; init; } = "";
    public string Issuer { get; init; } = "knovara-control-plane";
    public string Audience { get; init; } = "knovara-api";

    /// <summary>Must equal the API's Authentication:TenantClaim.</summary>
    public string TenantClaim { get; init; } = "tid";

    /// <summary>
    /// Short on purpose: revoking a device only takes effect when its current token expires, because the API
    /// validates tokens statelessly. 15 minutes bounds how long a revoked device keeps working.
    /// </summary>
    public int LifetimeMinutes { get; init; } = 15;

    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"Tokens:SigningKey must be at least {MinimumSigningKeyBytes} bytes and match the API's Authentication:SigningKey. "
                + "Supply it from configuration or a secret store, never from source.");
        }
        if (LifetimeMinutes is < 1 or > 24 * 60) throw new InvalidOperationException("Tokens:LifetimeMinutes must be between 1 and 1440.");
    }
}

public sealed class DataPlaneOptions
{
    /// <summary>Where enrolled desktops should send retrieval/ingestion requests (Knovara.Api).</summary>
    public string BaseUrl { get; init; } = "http://localhost:5299";
}

public sealed class ReleaseOptions
{
    /// <summary>Folder on this server's disk that holds the uploaded installers.</summary>
    public string Directory { get; init; } = "data/releases";

    public long MaxUploadBytes { get; init; } = 600L * 1024 * 1024;
}

public sealed class BootstrapOptions
{
    /// <summary>Creates the first platform account when the database has none. Supply from the environment; remove afterwards.</summary>
    public string? AdminEmail { get; init; }
    public string? AdminPassword { get; init; }
}
