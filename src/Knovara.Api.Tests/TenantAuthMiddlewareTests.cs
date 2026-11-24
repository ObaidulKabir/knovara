using System.Security.Claims;
using Knovara.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Knovara.Api.Tests;

public class TenantAuthMiddlewareTests
{
    private static DefaultHttpContext Context(string? header = null, params Claim[] claims)
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        if (header is not null) context.Request.Headers[TenantHeader.Name] = header;
        // An identity with a non-empty authentication type is what makes IsAuthenticated true.
        if (claims.Length > 0) context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return context;
    }

    private static (TenantAuthMiddleware Middleware, Func<bool> NextCalled) Build(TenantAuthOptions options)
    {
        var called = false;
        var middleware = new TenantAuthMiddleware(_ => { called = true; return Task.CompletedTask; }, options);
        return (middleware, () => called);
    }

    [Fact]
    public async Task Disabled_PassesHeaderThrough()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = false });
        var context = Context(header: "tenant-header");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal("tenant-header", context.Request.Headers[TenantHeader.Name]);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Enabled_SetsHeaderFromTokenClaim()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = true });
        var context = Context(claims: new Claim("tid", "tenant-123"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal("tenant-123", context.Request.Headers[TenantHeader.Name]);
    }

    /// <summary>The point of the middleware: a caller cannot reach another tenant by setting the header.</summary>
    [Fact]
    public async Task Enabled_TokenClaimOverwritesSpoofedHeader()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = true });
        var context = Context(header: "victim-tenant", claims: new Claim("tid", "attacker-tenant"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal("attacker-tenant", context.Request.Headers[TenantHeader.Name]);
    }

    [Fact]
    public async Task Enabled_RejectsUnauthenticatedRequest()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = true });
        var context = Context();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Enabled_HeaderAloneCannotAuthenticate()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = true });
        var context = Context(header: "tenant-header");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Enabled_RejectsTokenWithoutTenantClaim()
    {
        var (middleware, nextCalled) = Build(new TenantAuthOptions { Enabled = true });
        var context = Context(claims: new Claim(ClaimTypes.Name, "someone"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Enabled_UsesConfiguredClaimName()
    {
        var (middleware, _) = Build(new TenantAuthOptions { Enabled = true, TenantClaim = "tenant" });
        var context = Context(claims: new Claim("tenant", "from-custom-claim"));

        await middleware.InvokeAsync(context);

        Assert.Equal("from-custom-claim", context.Request.Headers[TenantHeader.Name]);
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        var (middleware, _) = Build(new TenantAuthOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!));
    }
}
