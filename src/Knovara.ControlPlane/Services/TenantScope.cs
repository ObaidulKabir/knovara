using System.Security.Claims;
using System.Text.RegularExpressions;
using Knovara.ControlPlane.Data;

namespace Knovara.ControlPlane.Services;

/// <summary>The signed-in dashboard user, read from the auth cookie.</summary>
public sealed record Principal(string AccountId, string Email, string Role, string? TenantId)
{
    public const string AccountClaim = "account";
    public const string TenantClaim = "tenant";

    public bool IsPlatform => Role == Roles.Platform;

    public static Principal? From(ClaimsPrincipal user)
    {
        var account = user.FindFirstValue(AccountClaim);
        var role = user.FindFirstValue(ClaimTypes.Role);
        if (account is null || role is null) return null;
        return new Principal(account, user.FindFirstValue(ClaimTypes.Email) ?? "", role, user.FindFirstValue(TenantClaim));
    }
}

public static partial class TenantScope
{
    /// <summary>
    /// Which tenant a dashboard request may act on. A tenant admin is pinned to their own tenant whatever the
    /// request names; only a platform operator may pick another, and must name one. Every tenant-scoped page and
    /// action goes through this, so a tampered query string or form field cannot reach another tenant's devices.
    /// </summary>
    public static bool TryResolve(Principal principal, string? requested, out string tenantId)
    {
        tenantId = "";
        if (principal.IsPlatform)
        {
            if (string.IsNullOrWhiteSpace(requested)) return false;
            tenantId = requested;
            return true;
        }

        if (principal.TenantId is null) return false;
        if (!string.IsNullOrWhiteSpace(requested) && requested != principal.TenantId) return false;
        tenantId = principal.TenantId;
        return true;
    }

    public static bool IsValidTenantId(string? tenantId) => tenantId is not null && TenantIdPattern().IsMatch(tenantId);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,62}$")]
    private static partial Regex TenantIdPattern();
}
