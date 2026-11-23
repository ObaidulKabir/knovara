namespace ICE.Api;

/// <summary>
/// The single place the API reads the caller's tenant from. Today the caller sends it; once
/// authentication exists, middleware must set it from the verified credential instead.
/// </summary>
public static class TenantHeader
{
    public const string Name = "X-Tenant-Id";
}
