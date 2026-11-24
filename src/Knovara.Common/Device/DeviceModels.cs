namespace Knovara.Common.Device;

/// <summary>What the control plane returns when a device enrolls or refreshes. <see cref="DeviceSecret"/> is only present on enrollment.</summary>
public sealed record DeviceSession(
    string DeviceId,
    string? DeviceSecret,
    string TenantId,
    string ApiBaseUrl,
    string AccessToken,
    DateTime ExpiresAt,
    string? LatestVersion);

/// <summary>What a desktop keeps on disk after enrolling: enough to ask the control plane for fresh access tokens.</summary>
public sealed record DeviceIdentity(
    string ControlPlaneUrl,
    string DeviceId,
    string DeviceSecret,
    string TenantId,
    string ApiBaseUrl);

public enum ControlPlaneFailure
{
    /// <summary>Could not connect, or the address is not a valid http(s) URL.</summary>
    Unreachable,
    /// <summary>The enrollment code, or another input, was refused (HTTP 400).</summary>
    Rejected,
    /// <summary>The tenant has no free device slot (HTTP 409).</summary>
    DeviceLimitReached,
    /// <summary>The device id or secret is not recognised (HTTP 401).</summary>
    BadCredentials,
    /// <summary>An administrator revoked this device (HTTP 403).</summary>
    Revoked,
    Unexpected
}

/// <summary>A failure talking to the control plane whose message is safe and useful to show to the user.</summary>
public sealed class ControlPlaneException(string message, ControlPlaneFailure failure) : Exception(message)
{
    public ControlPlaneFailure Failure { get; } = failure;
}
