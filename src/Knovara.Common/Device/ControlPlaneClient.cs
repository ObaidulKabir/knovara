using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Knovara.Common.Device;

/// <summary>Talks to the Knovara control plane's desktop-facing API: enroll with a code, then exchange the device secret for access tokens.</summary>
public sealed class ControlPlaneClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Trims and validates an address the user typed. Null if it is not an http(s) URL.</summary>
    public static string? NormalizeUrl(string? input)
    {
        var trimmed = input?.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? trimmed
            : null;
    }

    public Task<DeviceSession> EnrollAsync(string controlPlaneUrl, string code, string deviceName, string appVersion, string os, CancellationToken ct = default)
        => PostAsync(controlPlaneUrl, "api/enroll", new { code, deviceName, appVersion, os }, ct);

    public Task<DeviceSession> RefreshAsync(DeviceIdentity identity, string appVersion, string os, CancellationToken ct = default)
        => PostAsync(identity.ControlPlaneUrl, "api/device/token", new { identity.DeviceId, identity.DeviceSecret, appVersion, os }, ct);

    private async Task<DeviceSession> PostAsync(string controlPlaneUrl, string path, object body, CancellationToken ct)
    {
        var baseUrl = NormalizeUrl(controlPlaneUrl)
            ?? throw new ControlPlaneException("Enter the control plane address as an http:// or https:// URL.", ControlPlaneFailure.Unreachable);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"{baseUrl}/{path}", body, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ControlPlaneException($"Could not reach the control plane at {baseUrl}: {ex.Message}", ControlPlaneFailure.Unreachable);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ControlPlaneException($"The control plane at {baseUrl} did not respond in time.", ControlPlaneFailure.Unreachable);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DeviceSession>(Json, ct)
                    ?? throw new ControlPlaneException("The control plane returned an empty response.", ControlPlaneFailure.Unexpected);
            }

            var detail = await ReadDetailAsync(response, ct);
            throw response.StatusCode switch
            {
                HttpStatusCode.BadRequest => new ControlPlaneException(detail ?? "The request was rejected.", ControlPlaneFailure.Rejected),
                HttpStatusCode.Conflict => new ControlPlaneException(detail ?? "The tenant's device limit has been reached.", ControlPlaneFailure.DeviceLimitReached),
                HttpStatusCode.Unauthorized => new ControlPlaneException(detail ?? "This device is not recognised.", ControlPlaneFailure.BadCredentials),
                HttpStatusCode.Forbidden => new ControlPlaneException(detail ?? "This device has been revoked.", ControlPlaneFailure.Revoked),
                HttpStatusCode.TooManyRequests => new ControlPlaneException("Too many attempts. Wait a minute and try again.", ControlPlaneFailure.Rejected),
                _ => new ControlPlaneException($"The control plane answered {(int)response.StatusCode} {response.ReasonPhrase}.", ControlPlaneFailure.Unexpected)
            };
        }
    }

    /// <summary>The control plane answers errors as RFC 9457 problem details; its "detail" is written for end users.</summary>
    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
