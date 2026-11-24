using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knovara.Common.Device;
using Xunit;

namespace Knovara.Common.Tests;

internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, string? Body, string? Auth)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
        return respond(request, body);
    }
}

internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] protectedData) => protectedData.Select(b => (byte)(b ^ 0x5A)).ToArray();
}

internal sealed class RefusingProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] protectedData) => throw new System.Security.Cryptography.CryptographicException("wrong user");
}

internal static class Responses
{
    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        Json(status, JsonSerializer.Serialize(new { title = "t", status = (int)status, detail }));

    public static string Session(string token, DateTime expires, string? secret = null, string? latest = "1.2.0") =>
        JsonSerializer.Serialize(new
        {
            deviceId = "dev1", deviceSecret = secret, tenantId = "acme", apiBaseUrl = "http://api.test",
            accessToken = token, expiresAt = expires, latestVersion = latest
        });
}

public class ControlPlaneClientTests
{
    private static ControlPlaneClient Client(FakeHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Enroll_PostsTheCodeAndDeviceDetails_AndReturnsTheSession()
    {
        var handler = new FakeHandler((_, _) => Responses.Json(HttpStatusCode.OK,
            Responses.Session("tok", DateTime.UtcNow.AddMinutes(15), secret: "s3cret")));

        var session = await Client(handler).EnrollAsync("http://cp.test/", "KNV-AAAA-BBBB-CCCC", "Sam's PC", "1.0.0", "Windows 11");

        var call = Assert.Single(handler.Calls);
        Assert.Equal("http://cp.test/api/enroll", call.Uri.ToString());
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("KNV-AAAA-BBBB-CCCC", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Sam's PC", body.RootElement.GetProperty("deviceName").GetString());
        Assert.Equal("s3cret", session.DeviceSecret);
        Assert.Equal("acme", session.TenantId);
        Assert.Equal("1.2.0", session.LatestVersion);
    }

    [Fact]
    public async Task Refresh_SendsTheDeviceCredential()
    {
        var handler = new FakeHandler((_, _) => Responses.Json(HttpStatusCode.OK, Responses.Session("tok2", DateTime.UtcNow.AddMinutes(15))));
        var identity = new DeviceIdentity("http://cp.test", "dev1", "s3cret", "acme", "http://api.test");

        var session = await Client(handler).RefreshAsync(identity, "1.0.0", "Windows");

        Assert.Equal("http://cp.test/api/device/token", handler.Calls[0].Uri.ToString());
        using var body = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.Equal("dev1", body.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("s3cret", body.RootElement.GetProperty("deviceSecret").GetString());
        Assert.Equal("tok2", session.AccessToken);
        Assert.Null(session.DeviceSecret);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ControlPlaneFailure.Rejected, "The code is invalid")]
    [InlineData(HttpStatusCode.Conflict, ControlPlaneFailure.DeviceLimitReached, "Device limit reached")]
    [InlineData(HttpStatusCode.Unauthorized, ControlPlaneFailure.BadCredentials, "Not recognised")]
    [InlineData(HttpStatusCode.Forbidden, ControlPlaneFailure.Revoked, "Revoked by admin")]
    public async Task ServerRefusals_BecomeTypedErrorsCarryingTheServersExplanation(HttpStatusCode status, ControlPlaneFailure failure, string detail)
    {
        var handler = new FakeHandler((_, _) => Responses.Problem(status, detail));

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => Client(handler).EnrollAsync("http://cp.test", "c", "pc", "1", "w"));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(detail, ex.Message);
    }

    [Fact]
    public async Task TooManyRequests_AndUnexpectedStatuses_GetFriendlyMessages()
    {
        var limited = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            Client(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests))).EnrollAsync("http://cp.test", "c", "pc", "1", "w"));
        Assert.Contains("Too many attempts", limited.Message);

        var broken = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            Client(new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError))).EnrollAsync("http://cp.test", "c", "pc", "1", "w"));
        Assert.Equal(ControlPlaneFailure.Unexpected, broken.Failure);
    }

    [Fact]
    public async Task UnreachableServer_IsReportedWithTheAddressTried()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => Client(handler).EnrollAsync("http://cp.test", "c", "pc", "1", "w"));

        Assert.Equal(ControlPlaneFailure.Unreachable, ex.Failure);
        Assert.Contains("http://cp.test", ex.Message);
    }

    [Theory]
    [InlineData("ftp://cp.test")]
    [InlineData("cp.test")]
    [InlineData("")]
    public async Task NonHttpAddresses_AreRefusedBeforeAnyRequest(string url)
    {
        var handler = new FakeHandler((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<ControlPlaneException>(() => Client(handler).EnrollAsync(url, "c", "pc", "1", "w"));

        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(" https://cp.example.com/ ", "https://cp.example.com")]
    [InlineData("http://localhost:5400", "http://localhost:5400")]
    [InlineData("javascript:alert(1)", null)]
    public void NormalizeUrl(string input, string? expected) => Assert.Equal(expected, ControlPlaneClient.NormalizeUrl(input));
}

public class DeviceTokenProviderTests
{
    private static readonly DeviceIdentity Identity = new("http://cp.test", "dev1", "s3cret", "acme", "http://api.test");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private int _issued;

    private (DeviceTokenProvider Provider, FakeHandler Handler) Create(DeviceSession? initial = null)
    {
        var handler = new FakeHandler((_, _) => Responses.Json(HttpStatusCode.OK,
            Responses.Session($"token-{++_issued}", _clock.GetUtcNow().UtcDateTime.AddMinutes(15))));
        return (new DeviceTokenProvider(new ControlPlaneClient(new HttpClient(handler)), Identity, "1.0.0", "Windows", _clock, initial), handler);
    }

    [Fact]
    public async Task ReusesTheTokenUntilJustBeforeItExpires_ThenRefreshes()
    {
        var (provider, handler) = Create();

        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        _clock.Advance(TimeSpan.FromMinutes(13)); // 2 min left: still outside the 60 s margin
        Assert.Equal("token-1", await provider.GetAccessTokenAsync());
        Assert.Single(handler.Calls);

        _clock.Advance(TimeSpan.FromSeconds(70)); // inside the margin
        Assert.Equal("token-2", await provider.GetAccessTokenAsync());
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task AFreshEnrollmentSession_NeedsNoRefreshStraightAway()
    {
        var initial = new DeviceSession("dev1", "s3cret", "acme", "http://api.test", "enrolled-token", _clock.GetUtcNow().UtcDateTime.AddMinutes(15), "1.2.0");
        var (provider, handler) = Create(initial);

        Assert.Equal("enrolled-token", await provider.GetAccessTokenAsync());
        Assert.Empty(handler.Calls);
        Assert.Equal("1.2.0", provider.LatestVersion);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareASingleRefresh()
    {
        var (provider, handler) = Create();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetAccessTokenAsync()));

        Assert.All(tokens, t => Assert.Equal("token-1", t));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ARefusedRefresh_Propagates_AndALaterAttemptCanSucceed()
    {
        var revoked = true;
        var handler = new FakeHandler((_, _) => revoked
            ? Responses.Problem(HttpStatusCode.Forbidden, "This device has been revoked by an administrator.")
            : Responses.Json(HttpStatusCode.OK, Responses.Session("back", _clock.GetUtcNow().UtcDateTime.AddMinutes(15))));
        var provider = new DeviceTokenProvider(new ControlPlaneClient(new HttpClient(handler)), Identity, "1.0.0", "Windows", _clock);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => provider.GetAccessTokenAsync());
        Assert.Equal(ControlPlaneFailure.Revoked, ex.Failure);

        revoked = false; // an administrator restores the device
        Assert.Equal("back", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task BearerHandler_AttachesTheCurrentTokenToEveryRequest()
    {
        var (provider, _) = Create();
        var api = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(new BearerTokenHandler(provider) { InnerHandler = api });

        await http.GetAsync("http://api.test/api/resources");
        _clock.Advance(TimeSpan.FromMinutes(14)); // forces a refresh
        await http.GetAsync("http://api.test/api/resources");

        Assert.Equal(["Bearer token-1", "Bearer token-2"], api.Calls.Select(c => c.Auth).ToArray());
    }
}

public class DeviceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "knovara-dev-" + Guid.NewGuid().ToString("n"));
    private string FilePath => Path.Combine(_dir, "nested", "device.json");
    private static readonly DeviceIdentity Identity = new("http://cp.test", "dev1", "super-secret-value", "acme", "http://api.test");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndCreatesMissingFolders()
    {
        new DeviceStore(FilePath, new XorProtector()).Save(Identity);

        Assert.Equal(Identity, new DeviceStore(FilePath, new XorProtector()).Load());
    }

    [Fact]
    public void TheSecret_NeverAppearsInThePlainFile()
    {
        new DeviceStore(FilePath, new XorProtector()).Save(Identity);

        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("super-secret-value", text);
        Assert.Contains("dev1", text); // the non-secret parts stay readable for support
    }

    [Fact]
    public void Load_ReturnsNull_WhenMissing_Corrupt_OrSavedForAnotherUser()
    {
        var store = new DeviceStore(FilePath, new XorProtector());
        Assert.Null(store.Load());

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Null(store.Load());

        store.Save(Identity);
        Assert.Null(new DeviceStore(FilePath, new RefusingProtector()).Load());
    }

    [Fact]
    public void Delete_ForgetsTheDevice_AndIsSafeToRepeat()
    {
        var store = new DeviceStore(FilePath, new XorProtector());
        store.Save(Identity);

        store.Delete();
        store.Delete();

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_Overwrites_AndLeavesNoTempFile()
    {
        var store = new DeviceStore(FilePath, new XorProtector());
        store.Save(Identity);
        store.Save(Identity with { DeviceId = "dev2" });

        Assert.Equal("dev2", store.Load()!.DeviceId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }
}
