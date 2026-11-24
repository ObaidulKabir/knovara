namespace Knovara.Common.Device;

/// <summary>
/// Keeps a valid access token ready for the API. Tokens are short-lived on purpose (so that revoking a device
/// takes effect quickly), so this refreshes shortly before expiry, and concurrent callers share one refresh.
/// </summary>
public sealed class DeviceTokenProvider
{
    /// <summary>Refresh this long before expiry so a token never expires mid-request.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly ControlPlaneClient _client;
    private readonly DeviceIdentity _identity;
    private readonly string _appVersion;
    private readonly string _os;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceSession? _current;

    public DeviceTokenProvider(
        ControlPlaneClient client, DeviceIdentity identity, string appVersion, string os, TimeProvider? clock = null, DeviceSession? initial = null)
    {
        _client = client;
        _identity = identity;
        _appVersion = appVersion;
        _os = os;
        _clock = clock ?? TimeProvider.System;
        _current = initial;
    }

    public string TenantId => _identity.TenantId;
    public string ApiBaseUrl => _current?.ApiBaseUrl ?? _identity.ApiBaseUrl;
    public string ControlPlaneUrl => _identity.ControlPlaneUrl;

    /// <summary>The newest installer version the control plane has published, as of the last refresh. Null if none.</summary>
    public string? LatestVersion => _current?.LatestVersion;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (IsFresh(_current)) return _current!.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited for the gate.
            if (IsFresh(_current)) return _current!.AccessToken;

            _current = await _client.RefreshAsync(_identity, _appVersion, _os, ct);
            return _current.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(DeviceSession? session)
        => session is not null && session.ExpiresAt - RefreshMargin > _clock.GetUtcNow().UtcDateTime;
}

/// <summary>Adds the device's current access token to every request, refreshing it when needed.</summary>
public sealed class BearerTokenHandler(DeviceTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(ct));
        return await base.SendAsync(request, ct);
    }
}
