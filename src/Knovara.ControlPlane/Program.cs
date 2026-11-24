using System.Threading.RateLimiting;
using Knovara.ControlPlane.Data;
using Knovara.ControlPlane.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required. See README.md \"Control plane\".");

// Fail at startup, not on the first enrollment: a missing or short signing key would otherwise surface as a 500.
var tokens = builder.Configuration.GetSection("Tokens").Get<TokenOptions>() ?? new TokenOptions();
tokens.Validate();

builder.Services.AddSingleton(tokens);
builder.Services.AddSingleton(builder.Configuration.GetSection("DataPlane").Get<DataPlaneOptions>() ?? new DataPlaneOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Releases").Get<ReleaseOptions>() ?? new ReleaseOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Bootstrap").Get<BootstrapOptions>() ?? new BootstrapOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton<IControlPlaneStore, PostgresControlPlaneStore>();
builder.Services.AddSingleton<DeviceTokenIssuer>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<ReleaseService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "knovara.cp";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Secure whenever the request is HTTPS. Run this behind TLS in any real deployment (see README).
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy("Platform", policy => policy.RequireRole(Roles.Platform)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static RateLimitPartition<string> PerIp(HttpContext c, int permits) => RateLimitPartition.GetFixedWindowLimiter(
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });

    // Guessing a ~59-bit code or a 256-bit secret is hopeless, but there is no reason to allow it at speed.
    options.AddPolicy("enroll", c => PerIp(c, 10));
    options.AddPolicy("token", c => PerIp(c, 120));
    options.AddPolicy("login", c => PerIp(c, 20));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AuthorizePage("/Tenants", "Platform");
    options.Conventions.AddPageApplicationModelConvention("/Login",
        model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute("login")));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var store = app.Services.GetRequiredService<IControlPlaneStore>();
    await store.MigrateAsync();
    await Bootstrap.EnsureFirstAdminAsync(
        store, app.Services.GetRequiredService<AccountService>(),
        app.Services.GetRequiredService<BootstrapOptions>(), app.Logger);
}

app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ---- Desktop-facing API (no cookies; the device secret is the credential) ----

var api = app.MapGroup("/api");

api.MapPost("/enroll", async (EnrollmentRequest request, EnrollmentService enrollment, CancellationToken ct)
    => ToHttp(await enrollment.EnrollAsync(request, ct))).RequireRateLimiting("enroll");

api.MapPost("/device/token", async (TokenRequest request, EnrollmentService enrollment, CancellationToken ct)
    => ToHttp(await enrollment.RefreshAsync(request, ct))).RequireRateLimiting("token");

// Public by design: only version metadata, so a desktop can tell whether it is out of date.
api.MapGet("/releases/latest", async (IControlPlaneStore store, CancellationToken ct) =>
    await store.GetCurrentReleaseAsync(ct) is { } r
        ? Results.Ok(new { r.Version, r.Sha256, r.SizeBytes, r.FileName, r.UploadedAt })
        : Results.NotFound());

// ---- Installer download (signed-in users only) ----

app.MapGet("/download/{releaseId}", async (string releaseId, IControlPlaneStore store, ReleaseService releases, HttpContext http, CancellationToken ct) =>
{
    var release = await store.GetReleaseAsync(releaseId, ct);
    var path = release is null ? null : releases.PathFor(release);
    if (release is null || path is null || !File.Exists(path)) return Results.NotFound();

    http.Response.Headers["X-Content-SHA256"] = release.Sha256;
    return Results.File(File.OpenRead(path), "application/octet-stream", release.FileName, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapRazorPages();

app.Run();

static IResult ToHttp(SessionResult result) => result.Failure switch
{
    SessionFailure.None => Results.Ok(result.Session),
    SessionFailure.InvalidCode or SessionFailure.BadRequest => Results.Problem(result.Message, statusCode: 400, title: "Request rejected"),
    SessionFailure.DeviceLimitReached => Results.Problem(result.Message, statusCode: 409, title: "Device limit reached"),
    SessionFailure.Revoked => Results.Problem(result.Message, statusCode: 403, title: "Device revoked"),
    _ => Results.Problem(result.Message, statusCode: 401, title: "Unauthorized")
};

/// <summary>Needed so integration tests can reference the entry point.</summary>
public partial class Program;
