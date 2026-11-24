using Knovara.ControlPlane.Data;
using Npgsql;

namespace Knovara.ControlPlane.Tests;

/// <summary>Runs only when KNOVARA_TEST_POSTGRES holds a connection string; every test works inside its own freshly created tenant and removes it afterwards.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "KNOVARA_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a Postgres connection string to run the store tests.";
        }
    }
}

public sealed class PostgresStoreTests : IAsyncLifetime
{
    private NpgsqlDataSource? _data;
    private PostgresControlPlaneStore _store = null!;
    private readonly List<string> _tenants = [];
    private readonly List<string> _releases = [];
    private static readonly DateTime Now = DateTime.UtcNow;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(cs)) return;

        _data = NpgsqlDataSource.Create(cs);
        _store = new PostgresControlPlaneStore(_data);
        await _store.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_data is null) return;
        foreach (var t in _tenants)
        {
            await using var cmd = _data.CreateCommand("DELETE FROM cp_tenants WHERE tenant_id = @t"); // cascades to devices, codes, accounts
            cmd.Parameters.AddWithValue("t", t);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var v in _releases)
        {
            await using var cmd = _data.CreateCommand("DELETE FROM cp_releases WHERE version = @v");
            cmd.Parameters.AddWithValue("v", v);
            await cmd.ExecuteNonQueryAsync();
        }
        await _data.DisposeAsync();
    }

    private async Task<string> NewTenantAsync(int? limit = null)
    {
        var id = "test-" + Guid.NewGuid().ToString("n")[..10];
        _tenants.Add(id);
        Assert.True(await _store.CreateTenantAsync(id, "Test tenant", limit));
        return id;
    }

    private string _lastCodeId = "";

    private async Task<string> NewCodeAsync(string tenant, int maxUses = 1, DateTime? expires = null)
    {
        var code = Services.EnrollmentCodes.Generate();
        _lastCodeId = Guid.NewGuid().ToString("n");
        await _store.CreateCodeAsync(_lastCodeId, tenant,
            Services.EnrollmentCodes.Hash(Services.EnrollmentCodes.Normalize(code)!), null, "tester", expires ?? Now.AddDays(1), maxUses);
        return Services.EnrollmentCodes.Hash(Services.EnrollmentCodes.Normalize(code)!);
    }

    private Task<EnrollResult> EnrollAsync(string codeHash, string? name = null)
        => _store.EnrollDeviceAsync(codeHash, Guid.NewGuid().ToString("n"), name ?? "pc", "secret-hash", "Windows", "1.0.0", DateTime.UtcNow);

    [PostgresFact]
    public async Task Enroll_ConsumesTheCode_AndASingleUseCodeWorksOnce()
    {
        var tenant = await NewTenantAsync();
        var code = await NewCodeAsync(tenant);

        var first = await EnrollAsync(code);
        var second = await EnrollAsync(code);

        Assert.Equal(EnrollOutcome.Enrolled, first.Outcome);
        Assert.Equal(tenant, first.TenantId);
        Assert.Equal(EnrollOutcome.InvalidCode, second.Outcome);
        Assert.Single(await _store.ListDevicesAsync(tenant));
    }

    [PostgresFact]
    public async Task Enroll_RejectsUnknownExpiredAndRevokedCodes()
    {
        var tenant = await NewTenantAsync();
        var expired = await NewCodeAsync(tenant, expires: DateTime.UtcNow.AddMinutes(-1));
        var revoked = await NewCodeAsync(tenant);
        Assert.True(await _store.RevokeCodeAsync(tenant, _lastCodeId));

        Assert.Equal(EnrollOutcome.InvalidCode, (await EnrollAsync("0".PadLeft(64, '0'))).Outcome);
        Assert.Equal(EnrollOutcome.InvalidCode, (await EnrollAsync(expired)).Outcome);
        Assert.Equal(EnrollOutcome.InvalidCode, (await EnrollAsync(revoked)).Outcome);
        Assert.Empty(await _store.ListDevicesAsync(tenant));
    }

    [PostgresFact]
    public async Task DeviceLimit_BlocksEnrollment_WithoutBurningTheCode_UntilADeviceIsRevoked()
    {
        var tenant = await NewTenantAsync(limit: 1);
        Assert.Equal(EnrollOutcome.Enrolled, (await EnrollAsync(await NewCodeAsync(tenant))).Outcome);

        var second = await NewCodeAsync(tenant);
        var secondId = _lastCodeId;
        Assert.Equal(EnrollOutcome.DeviceLimitReached, (await EnrollAsync(second)).Outcome);
        Assert.Equal(0, (await _store.ListCodesAsync(tenant)).Single(c => c.CodeId == secondId).Uses); // refused enrollments do not burn the code

        var device = (await _store.ListDevicesAsync(tenant)).Single();
        Assert.True(await _store.SetDeviceStatusAsync(tenant, device.DeviceId, DeviceStatus.Revoked));

        Assert.Equal(EnrollOutcome.Enrolled, (await EnrollAsync(second)).Outcome); // same code now works
    }

    [PostgresFact]
    public async Task ConcurrentEnrollments_NeverExceedTheDeviceLimit()
    {
        var tenant = await NewTenantAsync(limit: 3);
        var code = await NewCodeAsync(tenant, maxUses: 20);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => EnrollAsync(code))));

        Assert.Equal(3, results.Count(r => r.Outcome == EnrollOutcome.Enrolled));
        Assert.Equal(9, results.Count(r => r.Outcome == EnrollOutcome.DeviceLimitReached));
        Assert.Equal(3, (await _store.ListDevicesAsync(tenant)).Count);
        Assert.Equal(3, (await _store.ListCodesAsync(tenant)).Single().Uses);
    }

    [PostgresFact]
    public async Task ConcurrentEnrollments_WithOneSingleUseCode_EnrollExactlyOneDevice()
    {
        var tenant = await NewTenantAsync();
        var code = await NewCodeAsync(tenant);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => EnrollAsync(code))));

        Assert.Equal(1, results.Count(r => r.Outcome == EnrollOutcome.Enrolled));
        Assert.Single(await _store.ListDevicesAsync(tenant));
    }

    [PostgresFact]
    public async Task DeviceStatus_CanOnlyBeChangedInsideItsOwnTenant()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        await EnrollAsync(await NewCodeAsync(a));
        var device = (await _store.ListDevicesAsync(a)).Single();

        Assert.False(await _store.SetDeviceStatusAsync(b, device.DeviceId, DeviceStatus.Revoked));
        Assert.Equal(DeviceStatus.Active, (await _store.ListDevicesAsync(a)).Single().Status);
        Assert.False(await _store.RevokeCodeAsync(b, (await _store.ListCodesAsync(a)).Single().CodeId));
    }

    [PostgresFact]
    public async Task Credential_ReflectsRevocation()
    {
        var tenant = await NewTenantAsync();
        await EnrollAsync(await NewCodeAsync(tenant));
        var device = (await _store.ListDevicesAsync(tenant)).Single();

        Assert.Equal(DeviceStatus.Active, (await _store.GetDeviceCredentialAsync(device.DeviceId))!.Status);
        await _store.SetDeviceStatusAsync(tenant, device.DeviceId, DeviceStatus.Revoked);
        var after = await _store.GetDeviceCredentialAsync(device.DeviceId);
        Assert.Equal(DeviceStatus.Revoked, after!.Status);
        Assert.Equal(tenant, after.TenantId);
        Assert.Null(await _store.GetDeviceCredentialAsync("nope"));
    }

    [PostgresFact]
    public async Task Accounts_AreUniqueByEmailCaseInsensitively_AndRolesAreConsistentWithTenants()
    {
        var tenant = await NewTenantAsync();
        var email = $"admin-{Guid.NewGuid():n}@example.test";

        Assert.True(await _store.CreateAccountAsync(Guid.NewGuid().ToString("n"), email, "hash", Roles.Tenant, tenant));
        Assert.False(await _store.CreateAccountAsync(Guid.NewGuid().ToString("n"), email.ToUpperInvariant(), "hash", Roles.Tenant, tenant));
        Assert.Equal(tenant, (await _store.FindAccountByEmailAsync(email.ToUpperInvariant()))!.TenantId);

        // A platform account must not belong to a tenant, and a tenant account must.
        await Assert.ThrowsAsync<PostgresException>(() => _store.CreateAccountAsync(Guid.NewGuid().ToString("n"), $"x-{Guid.NewGuid():n}@example.test", "h", Roles.Platform, tenant));
        await Assert.ThrowsAsync<PostgresException>(() => _store.CreateAccountAsync(Guid.NewGuid().ToString("n"), $"y-{Guid.NewGuid():n}@example.test", "h", Roles.Tenant, null));
    }

    [PostgresFact]
    public async Task LoginFailures_LockTheAccountAtTheThreshold_AndResetClearsIt()
    {
        var tenant = await NewTenantAsync();
        var email = $"lock-{Guid.NewGuid():n}@example.test";
        var id = Guid.NewGuid().ToString("n");
        await _store.CreateAccountAsync(id, email, "hash", Roles.Tenant, tenant);

        for (var i = 0; i < 4; i++) await _store.RecordLoginFailureAsync(id, 5, TimeSpan.FromMinutes(15));
        Assert.Null((await _store.GetAccountAsync(id))!.LockedUntil);

        await _store.RecordLoginFailureAsync(id, 5, TimeSpan.FromMinutes(15));
        var locked = await _store.GetAccountAsync(id);
        Assert.True(locked!.LockedUntil > DateTime.UtcNow.AddMinutes(10));

        await _store.ResetLoginFailuresAsync(id);
        var cleared = await _store.GetAccountAsync(id);
        Assert.Equal(0, cleared!.FailedAttempts);
        Assert.Null(cleared.LockedUntil);
    }

    [PostgresFact]
    public async Task Tenants_RejectDuplicatesAndInvalidIds()
    {
        var id = await NewTenantAsync();
        Assert.False(await _store.CreateTenantAsync(id, "again", null));
        await Assert.ThrowsAsync<PostgresException>(() => _store.CreateTenantAsync("Bad Id!", "x", null));
    }

    [PostgresFact]
    public async Task Releases_HaveUniqueVersions_AndExactlyOneCurrent()
    {
        var v1 = $"9{Random.Shared.Next(100, 999)}.0.1";
        var v2 = $"9{Random.Shared.Next(100, 999)}.0.2";
        _releases.AddRange([v1, v2]);
        Release Make(string v) => new(Guid.NewGuid().ToString("n"), v, "s.exe", new string('a', 64), 10, null, "t", DateTime.UtcNow, false);
        var r1 = Make(v1); var r2 = Make(v2);

        Assert.True(await _store.AddReleaseAsync(r1));
        Assert.False(await _store.AddReleaseAsync(Make(v1)));
        Assert.True(await _store.AddReleaseAsync(r2));

        await _store.SetCurrentReleaseAsync(r1.ReleaseId);
        await _store.SetCurrentReleaseAsync(r2.ReleaseId);

        Assert.Equal(r2.ReleaseId, (await _store.GetCurrentReleaseAsync())!.ReleaseId);
        Assert.Single(await _store.ListReleasesAsync(), r => r.IsCurrent);
        Assert.True(await _store.DeleteReleaseAsync(r2.ReleaseId));
    }
}
