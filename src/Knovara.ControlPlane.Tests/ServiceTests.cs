using System.Security.Claims;
using Knovara.ControlPlane.Data;
using Knovara.ControlPlane.Services;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.Text;

namespace Knovara.ControlPlane.Tests;

public class EnrollmentCodeTests
{
    [Fact]
    public void Generate_ProducesGroupedCodesFromTheUnambiguousAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = EnrollmentCodes.Generate();
            Assert.Matches("^KNV-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
            Assert.DoesNotContain(code[4..], new[] { "0", "O", "1", "I", "L" }.Where(code[4..].Contains));
            Assert.NotNull(EnrollmentCodes.Normalize(code));
        }
    }

    [Fact]
    public void Generate_IsNotRepeating()
        => Assert.Equal(500, Enumerable.Range(0, 500).Select(_ => EnrollmentCodes.Generate()).Distinct().Count());

    [Theory]
    [InlineData("KNV-7QH2-M9XD-4TRK", "7QH2M9XD4TRK")]
    [InlineData("  knv 7qh2 m9xd 4trk  ", "7QH2M9XD4TRK")]
    [InlineData("7qh2m9xd4trk", "7QH2M9XD4TRK")]
    public void Normalize_AcceptsHowPeopleActuallyTypeCodes(string input, string expected)
        => Assert.Equal(expected, EnrollmentCodes.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KNV-SHORT")]
    [InlineData("KNV-7QH2-M9XD-4TRKX")]      // too long
    [InlineData("KNV-0OIL-0OIL-0OIL")]       // ambiguous characters are not in the alphabet
    public void Normalize_RejectsAnythingThatCannotBeACode(string? input)
        => Assert.Null(EnrollmentCodes.Normalize(input));

    [Fact]
    public void Hash_IsStableAndHidesTheCode()
    {
        var hash = EnrollmentCodes.Hash("7QH2M9XD4TRK");
        Assert.Equal(hash, EnrollmentCodes.Hash("7QH2M9XD4TRK"));
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("7QH2", hash, StringComparison.OrdinalIgnoreCase);
    }
}

public class DeviceSecretTests
{
    [Fact]
    public void Verify_AcceptsTheRightSecretOnly()
    {
        var secret = DeviceSecrets.Generate();
        var stored = DeviceSecrets.Hash(secret);

        Assert.True(DeviceSecrets.Verify(secret, stored));
        Assert.False(DeviceSecrets.Verify(secret + "x", stored));
        Assert.False(DeviceSecrets.Verify(DeviceSecrets.Generate(), stored));
    }

    [Fact]
    public void Generate_Is256BitsAndUrlSafe()
    {
        var secret = DeviceSecrets.Generate();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.NotEqual(secret, DeviceSecrets.Generate());
    }
}

public class DeviceTokenIssuerTests
{
    private const string Key = "0123456789abcdef0123456789abcdef-test-key";
    private static readonly TokenOptions Options = new() { SigningKey = Key, LifetimeMinutes = 15 };

    private static TokenValidationParameters ParametersFor(string key, TokenOptions o) => new()
    {
        ValidIssuer = o.Issuer, ValidAudience = o.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true
    };

    [Fact]
    public async Task Token_CarriesTheTenantAndDevice_AndValidatesLikeTheApiDoes()
    {
        // Validation uses the real clock, so the token is issued "now", like the API sees it.
        var issuedAt = DateTimeOffset.UtcNow;
        var token = new DeviceTokenIssuer(Options, new FakeTimeProvider(issuedAt)).Issue("dev-1", "acme");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, ParametersFor(Key, Options));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("acme", result.Claims["tid"]);
        Assert.Equal("dev-1", result.Claims["did"]);
        Assert.Equal(issuedAt.UtcDateTime.AddMinutes(15), token.ExpiresAt);
    }

    [Fact]
    public async Task Token_IsRejectedWithAnotherKey_AfterExpiry_AndForAnotherAudience()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var token = new DeviceTokenIssuer(Options, clock).Issue("dev-1", "acme").AccessToken;
        var handler = new JsonWebTokenHandler();

        Assert.False((await handler.ValidateTokenAsync(token, ParametersFor("a-completely-different-32-byte-key!!", Options))).IsValid);

        var expired = new DeviceTokenIssuer(Options, new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-2))).Issue("dev-1", "acme").AccessToken;
        Assert.False((await handler.ValidateTokenAsync(expired, ParametersFor(Key, Options))).IsValid);

        Assert.False((await handler.ValidateTokenAsync(token, ParametersFor(Key, new TokenOptions { SigningKey = Key, Audience = "someone-else" }))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Validate_RefusesWeakSigningKeys(string key)
        => Assert.Throws<InvalidOperationException>(() => new TokenOptions { SigningKey = key }.Validate());
}

public class TenantScopeTests
{
    private static Principal Platform => new("a1", "ops@x.io", Roles.Platform, null);
    private static Principal AcmeAdmin => new("a2", "admin@acme.io", Roles.Tenant, "acme");

    [Fact]
    public void TenantAdmin_IsPinnedToTheirOwnTenant_WhateverTheRequestNames()
    {
        Assert.True(TenantScope.TryResolve(AcmeAdmin, null, out var own));
        Assert.Equal("acme", own);
        Assert.True(TenantScope.TryResolve(AcmeAdmin, "acme", out _));
        Assert.False(TenantScope.TryResolve(AcmeAdmin, "globex", out _));
    }

    [Fact]
    public void Platform_MustNameATenant_AndMayPickAny()
    {
        Assert.False(TenantScope.TryResolve(Platform, null, out _));
        Assert.False(TenantScope.TryResolve(Platform, "  ", out _));
        Assert.True(TenantScope.TryResolve(Platform, "globex", out var picked));
        Assert.Equal("globex", picked);
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("acme-2_x", true)]
    [InlineData("a", false)]
    [InlineData("Acme", false)]
    [InlineData("-acme", false)]
    [InlineData("acme corp", false)]
    [InlineData("acme'; drop table", false)]
    [InlineData(null, false)]
    public void IsValidTenantId(string? id, bool expected) => Assert.Equal(expected, TenantScope.IsValidTenantId(id));

    [Fact]
    public void Principal_RoundTripsThroughClaims()
    {
        var identity = new ClaimsIdentity(
            [new(Principal.AccountClaim, "a2"), new(ClaimTypes.Email, "admin@acme.io"), new(ClaimTypes.Role, Roles.Tenant), new(Principal.TenantClaim, "acme")], "test");
        Assert.Equal(AcmeAdmin with { AccountId = "a2" }, Principal.From(new ClaimsPrincipal(identity)));
        Assert.Null(Principal.From(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

public class AccountServiceTests
{
    private readonly Mock<IControlPlaneStore> _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private AccountService Service => new(_store.Object, _clock);

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactly12chr", true)]
    public void PasswordProblem_EnforcesMinimumLength(string password, bool ok)
        => Assert.Equal(ok, AccountService.PasswordProblem(password) is null);

    [Fact]
    public void PasswordProblem_RefusesThePasswordBeingTheEmail()
        => Assert.NotNull(AccountService.PasswordProblem("Admin@Example.com", "admin@example.com"));

    [Theory]
    [InlineData(" Admin@Example.COM ", "admin@example.com")]
    [InlineData("nope", null)]
    [InlineData("a@b", null)]
    public void NormalizeEmail(string input, string? expected) => Assert.Equal(expected, AccountService.NormalizeEmail(input));

    [Fact]
    public async Task Create_StoresAHashNeverThePassword()
    {
        string? stored = null;
        _store.Setup(s => s.CreateAccountAsync(It.IsAny<string>(), "a@b.io", It.IsAny<string>(), Roles.Platform, null, It.IsAny<CancellationToken>()))
              .Callback<string, string, string, string, string?, CancellationToken>((_, _, hash, _, _, _) => stored = hash)
              .ReturnsAsync(true);

        var outcome = await Service.CreateAsync("A@b.io", "a-long-enough-password", Roles.Platform, null);

        Assert.Equal(CreateAccountOutcome.Created, outcome);
        Assert.NotNull(stored);
        Assert.DoesNotContain("a-long-enough-password", stored);
    }

    [Fact]
    public async Task Create_RejectsBadInputWithoutTouchingTheStore()
    {
        Assert.Equal(CreateAccountOutcome.InvalidEmail, await Service.CreateAsync("nope", "a-long-enough-password", Roles.Platform, null));
        Assert.Equal(CreateAccountOutcome.WeakPassword, await Service.CreateAsync("a@b.io", "short", Roles.Platform, null));
        _store.Verify(s => s.CreateAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task<Account> StoredAccountAsync(string password, int failed = 0, DateTime? lockedUntil = null)
    {
        string? hash = null;
        _store.Setup(s => s.CreateAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
              .Callback<string, string, string, string, string?, CancellationToken>((_, _, h, _, _, _) => hash = h).ReturnsAsync(true);
        await Service.CreateAsync("a@b.io", password, Roles.Platform, null);
        var account = new Account("id1", "a@b.io", hash!, Roles.Platform, null, failed, lockedUntil);
        _store.Setup(s => s.FindAccountByEmailAsync("a@b.io", It.IsAny<CancellationToken>())).ReturnsAsync(account);
        return account;
    }

    [Fact]
    public async Task SignIn_WithTheRightPassword_Succeeds_AndClearsEarlierFailures()
    {
        await StoredAccountAsync("correct-horse-battery", failed: 2);

        var result = await Service.SignInAsync("A@B.io", "correct-horse-battery");

        Assert.Equal(SignInOutcome.Success, result.Outcome);
        _store.Verify(s => s.ResetLoginFailuresAsync("id1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SignIn_WithAWrongPassword_IsCountedTowardsLockout()
    {
        await StoredAccountAsync("correct-horse-battery");

        var result = await Service.SignInAsync("a@b.io", "wrong-wrong-wrong");

        Assert.Equal(SignInOutcome.InvalidCredentials, result.Outcome);
        _store.Verify(s => s.RecordLoginFailureAsync("id1", AccountService.MaxFailedAttempts, AccountService.LockoutDuration, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SignIn_WhileLocked_IsRefusedEvenWithTheRightPassword()
    {
        await StoredAccountAsync("correct-horse-battery", failed: 5, lockedUntil: _clock.GetUtcNow().UtcDateTime.AddMinutes(10));

        Assert.Equal(SignInOutcome.LockedOut, (await Service.SignInAsync("a@b.io", "correct-horse-battery")).Outcome);

        _clock.Advance(TimeSpan.FromMinutes(11)); // lock expires
        Assert.Equal(SignInOutcome.Success, (await Service.SignInAsync("a@b.io", "correct-horse-battery")).Outcome);
    }

    [Fact]
    public async Task SignIn_UnknownEmail_LooksLikeAWrongPassword()
    {
        _store.Setup(s => s.FindAccountByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Account?)null);

        Assert.Equal(SignInOutcome.InvalidCredentials, (await Service.SignInAsync("ghost@b.io", "whatever-password")).Outcome);
        Assert.Equal(SignInOutcome.InvalidCredentials, (await Service.SignInAsync("not-an-email", "whatever-password")).Outcome);
    }
}

public class EnrollmentServiceTests
{
    private const string Key = "0123456789abcdef0123456789abcdef-test-key";
    private readonly Mock<IControlPlaneStore> _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private EnrollmentService Service => new(
        _store.Object, new DeviceTokenIssuer(new TokenOptions { SigningKey = Key }, _clock),
        new DataPlaneOptions { BaseUrl = "http://api.test" }, _clock);

    [Fact]
    public async Task Enroll_Success_ReturnsTheSecretOnce_AndStoresOnlyItsHash()
    {
        string? storedHash = null, storedCodeHash = null;
        _store.Setup(s => s.EnrollDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), "Sam's laptop", It.IsAny<string>(), "Windows", "1.0.0", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
              .Callback<string, string, string, string, string?, string?, DateTime, CancellationToken>((codeHash, _, _, secretHash, _, _, _, _) => { storedCodeHash = codeHash; storedHash = secretHash; })
              .ReturnsAsync(new EnrollResult(EnrollOutcome.Enrolled, "acme"));
        _store.Setup(s => s.GetCurrentReleaseAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Release("r", "1.2.0", "f.exe", "h", 1, null, null, DateTime.UtcNow, true));

        var result = await Service.EnrollAsync(new EnrollmentRequest("KNV-7QH2-M9XD-4TRK", "Sam's laptop", "1.0.0", "Windows"));

        Assert.Equal(SessionFailure.None, result.Failure);
        var session = result.Session!;
        Assert.Equal("acme", session.TenantId);
        Assert.Equal("http://api.test", session.ApiBaseUrl);
        Assert.Equal("1.2.0", session.LatestVersion);
        Assert.True(DeviceSecrets.Verify(session.DeviceSecret!, storedHash!));
        Assert.DoesNotContain(session.DeviceSecret!, storedHash);
        Assert.Equal(EnrollmentCodes.Hash("7QH2M9XD4TRK"), storedCodeHash); // normalised before hashing
        Assert.False(string.IsNullOrEmpty(session.AccessToken));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(null)]
    public async Task Enroll_MalformedCode_NeverReachesTheStore(string? code)
    {
        var result = await Service.EnrollAsync(new EnrollmentRequest(code, "pc", null, null));

        Assert.Equal(SessionFailure.InvalidCode, result.Failure);
        _store.Verify(s => s.EnrollDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enroll_RequiresADeviceName()
        => Assert.Equal(SessionFailure.BadRequest, (await Service.EnrollAsync(new EnrollmentRequest("KNV-7QH2-M9XD-4TRK", "  ", null, null))).Failure);

    [Theory]
    [InlineData(EnrollOutcome.InvalidCode, SessionFailure.InvalidCode)]
    [InlineData(EnrollOutcome.DeviceLimitReached, SessionFailure.DeviceLimitReached)]
    public async Task Enroll_StoreRefusals_MapToFailures(EnrollOutcome outcome, SessionFailure expected)
    {
        _store.Setup(s => s.EnrollDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new EnrollResult(outcome, "acme"));

        var result = await Service.EnrollAsync(new EnrollmentRequest("KNV-7QH2-M9XD-4TRK", "pc", null, null));

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Session);
    }

    private void StoreDevice(string secret, string status)
        => _store.Setup(s => s.GetDeviceCredentialAsync("dev1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new DeviceCredential("dev1", "acme", DeviceSecrets.Hash(secret), status));

    [Fact]
    public async Task Refresh_WithTheRightSecret_IssuesATenantTokenAndRecordsTheCheckIn()
    {
        StoreDevice("s3cret", DeviceStatus.Active);

        var result = await Service.RefreshAsync(new TokenRequest("dev1", "s3cret", "1.1.0", "Windows 11"));

        Assert.Equal(SessionFailure.None, result.Failure);
        Assert.Equal("acme", result.Session!.TenantId);
        Assert.Null(result.Session.DeviceSecret); // the secret is never echoed back
        _store.Verify(s => s.TouchDeviceAsync("dev1", "1.1.0", "Windows 11", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_UnknownDeviceAndWrongSecret_AreIndistinguishable()
    {
        StoreDevice("s3cret", DeviceStatus.Active);
        _store.Setup(s => s.GetDeviceCredentialAsync("ghost", It.IsAny<CancellationToken>())).ReturnsAsync((DeviceCredential?)null);

        var wrong = await Service.RefreshAsync(new TokenRequest("dev1", "nope", null, null));
        var unknown = await Service.RefreshAsync(new TokenRequest("ghost", "s3cret", null, null));

        Assert.Equal(SessionFailure.InvalidCredentials, wrong.Failure);
        Assert.Equal(wrong.Failure, unknown.Failure);
        Assert.Equal(wrong.Message, unknown.Message);
        _store.Verify(s => s.TouchDeviceAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_ForARevokedDevice_IsRefusedEvenWithTheRightSecret()
    {
        StoreDevice("s3cret", DeviceStatus.Revoked);

        var result = await Service.RefreshAsync(new TokenRequest("dev1", "s3cret", null, null));

        Assert.Equal(SessionFailure.Revoked, result.Failure);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task Refresh_RequiresBothFields()
    {
        Assert.Equal(SessionFailure.BadRequest, (await Service.RefreshAsync(new TokenRequest(null, "x", null, null))).Failure);
        Assert.Equal(SessionFailure.BadRequest, (await Service.RefreshAsync(new TokenRequest("dev1", "", null, null))).Failure);
    }
}

public class ReleaseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "knovara-rel-" + Guid.NewGuid().ToString("n"));
    private readonly Mock<IControlPlaneStore> _store = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private ReleaseService Service(long max = 10_000) => new(_store.Object, new ReleaseOptions { Directory = _dir, MaxUploadBytes = max }, _clock);

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("12.34.56-beta.1", true)]
    [InlineData("1.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.0.0; rm -rf", false)]
    [InlineData(null, false)]
    public void IsValidVersion(string? v, bool expected) => Assert.Equal(expected, ReleaseService.IsValidVersion(v));

    [Theory]
    [InlineData("Knovara-Setup.exe", "Knovara-Setup.exe")]
    [InlineData("C:\\temp\\evil\\setup.EXE", "setup.EXE")]
    [InlineData("../../etc/setup.zip", "setup.zip")]
    [InlineData("my setup (1).msi", "my_setup__1_.msi")]
    public void SafeFileName_KeepsOnlyASanitisedLeaf(string input, string expected) => Assert.Equal(expected, ReleaseService.SafeFileName(input));

    [Theory]
    [InlineData("setup.sh")]
    [InlineData("setup.exe.txt")]
    [InlineData(".exe")]
    [InlineData("")]
    [InlineData(null)]
    public void SafeFileName_RefusesOtherFileTypes(string? input) => Assert.Null(ReleaseService.SafeFileName(input));

    [Fact]
    public async Task Add_StoresTheFileUnderAGeneratedFolder_WithItsRealSha256_AndMakesTheFirstOneCurrent()
    {
        var bytes = Encoding.UTF8.GetBytes("installer bytes");
        Release? added = null;
        _store.Setup(s => s.AddReleaseAsync(It.IsAny<Release>(), It.IsAny<CancellationToken>()))
              .Callback<Release, CancellationToken>((r, _) => added = r).ReturnsAsync(true);
        _store.Setup(s => s.GetCurrentReleaseAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Release?)null);

        var result = await Service().AddAsync(new MemoryStream(bytes), "..\\..\\Setup.exe", "1.0.0", " first ", "ops@x.io");

        Assert.Equal(AddReleaseOutcome.Added, result.Outcome);
        Assert.Equal("Setup.exe", added!.FileName);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), added.Sha256);
        Assert.Equal(bytes.Length, added.SizeBytes);
        Assert.Equal("first", added.Notes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_dir, added.ReleaseId, "Setup.exe")));
        _store.Verify(s => s.SetCurrentReleaseAsync(added.ReleaseId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_DoesNotStealCurrent_WhenAReleaseIsAlreadyCurrent()
    {
        _store.Setup(s => s.AddReleaseAsync(It.IsAny<Release>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _store.Setup(s => s.GetCurrentReleaseAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Release("old", "0.9.0", "f.exe", "h", 1, null, null, DateTime.UtcNow, true));

        await Service().AddAsync(new MemoryStream([1, 2, 3]), "s.exe", "1.0.0", null, "ops");

        _store.Verify(s => s.SetCurrentReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Add_RejectsDuplicateVersionTooLargeAndEmpty_LeavingNoFilesBehind()
    {
        _store.Setup(s => s.AddReleaseAsync(It.IsAny<Release>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.Equal(AddReleaseOutcome.DuplicateVersion, (await Service().AddAsync(new MemoryStream([1]), "s.exe", "1.0.0", null, "ops")).Outcome);
        Assert.Equal(AddReleaseOutcome.TooLarge, (await Service(max: 5).AddAsync(new MemoryStream(new byte[100]), "s.exe", "1.0.1", null, "ops")).Outcome);
        Assert.Equal(AddReleaseOutcome.InvalidFile, (await Service().AddAsync(new MemoryStream([]), "s.exe", "1.0.2", null, "ops")).Outcome);
        Assert.Equal(AddReleaseOutcome.InvalidVersion, (await Service().AddAsync(new MemoryStream([1]), "s.exe", "nope", null, "ops")).Outcome);
        Assert.Equal(AddReleaseOutcome.InvalidFile, (await Service().AddAsync(new MemoryStream([1]), "s.txt", "1.0.3", null, "ops")).Outcome);

        Assert.Empty(Directory.Exists(_dir) ? Directory.GetDirectories(_dir) : []);
    }
}
