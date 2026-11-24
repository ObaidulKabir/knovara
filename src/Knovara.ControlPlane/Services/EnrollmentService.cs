using Knovara.ControlPlane.Data;

namespace Knovara.ControlPlane.Services;

public sealed record EnrollmentRequest(string? Code, string? DeviceName, string? AppVersion, string? Os);
public sealed record TokenRequest(string? DeviceId, string? DeviceSecret, string? AppVersion, string? Os);

/// <summary>What a desktop receives: where to send requests, a token to send them with, and (when enrolling) its long-lived secret.</summary>
public sealed record DeviceSession(
    string DeviceId, string? DeviceSecret, string TenantId, string ApiBaseUrl,
    string AccessToken, DateTime ExpiresAt, string? LatestVersion);

public enum SessionFailure { None, InvalidCode, DeviceLimitReached, BadRequest, InvalidCredentials, Revoked }

public sealed record SessionResult(DeviceSession? Session, SessionFailure Failure, string? Message = null)
{
    public static SessionResult Ok(DeviceSession s) => new(s, SessionFailure.None);
    public static SessionResult Fail(SessionFailure f, string message) => new(null, f, message);
}

/// <summary>Registers devices from enrollment codes and exchanges a device's secret for short-lived access tokens.</summary>
public sealed class EnrollmentService(
    IControlPlaneStore store, DeviceTokenIssuer issuer, DataPlaneOptions dataPlane, TimeProvider clock)
{
    private const int MaxNameLength = 80;

    public async Task<SessionResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default)
    {
        var code = EnrollmentCodes.Normalize(request.Code);
        var name = Clean(request.DeviceName, MaxNameLength);
        if (name is null) return SessionResult.Fail(SessionFailure.BadRequest, "A device name is required.");

        // A malformed code is rejected before touching the database; a well-formed wrong one gets the same
        // answer as an expired or used one, so the endpoint does not reveal which codes exist.
        if (code is null) return SessionResult.Fail(SessionFailure.InvalidCode, InvalidCodeMessage);

        var deviceId = Guid.NewGuid().ToString("n");
        var secret = DeviceSecrets.Generate();
        var now = clock.GetUtcNow().UtcDateTime;

        var enrolled = await store.EnrollDeviceAsync(
            EnrollmentCodes.Hash(code), deviceId, name, DeviceSecrets.Hash(secret),
            Clean(request.Os, 80), Clean(request.AppVersion, 40), now, ct);

        switch (enrolled.Outcome)
        {
            case EnrollOutcome.InvalidCode:
                return SessionResult.Fail(SessionFailure.InvalidCode, InvalidCodeMessage);
            case EnrollOutcome.DeviceLimitReached:
                return SessionResult.Fail(SessionFailure.DeviceLimitReached, "This tenant has reached its device limit. Ask your administrator to revoke an unused device.");
        }

        return SessionResult.Ok(await BuildSessionAsync(deviceId, secret, enrolled.TenantId!, ct));
    }

    public async Task<SessionResult> RefreshAsync(TokenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrEmpty(request.DeviceSecret))
        {
            return SessionResult.Fail(SessionFailure.BadRequest, "deviceId and deviceSecret are required.");
        }

        var credential = await store.GetDeviceCredentialAsync(request.DeviceId, ct);

        // Unknown device and wrong secret are indistinguishable to the caller.
        if (credential is null || !DeviceSecrets.Verify(request.DeviceSecret, credential.SecretHash))
        {
            return SessionResult.Fail(SessionFailure.InvalidCredentials, "Unknown device or wrong secret.");
        }

        if (credential.Status != DeviceStatus.Active)
        {
            return SessionResult.Fail(SessionFailure.Revoked, "This device has been revoked by an administrator.");
        }

        await store.TouchDeviceAsync(credential.DeviceId, Clean(request.AppVersion, 40), Clean(request.Os, 80), clock.GetUtcNow().UtcDateTime, ct);
        return SessionResult.Ok(await BuildSessionAsync(credential.DeviceId, null, credential.TenantId, ct));
    }

    private async Task<DeviceSession> BuildSessionAsync(string deviceId, string? secret, string tenantId, CancellationToken ct)
    {
        var token = issuer.Issue(deviceId, tenantId);
        var latest = await store.GetCurrentReleaseAsync(ct);
        return new DeviceSession(deviceId, secret, tenantId, dataPlane.BaseUrl, token.AccessToken, token.ExpiresAt, latest?.Version);
    }

    private const string InvalidCodeMessage = "The enrollment code is invalid, expired, revoked or already used.";

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Any(char.IsControl)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
