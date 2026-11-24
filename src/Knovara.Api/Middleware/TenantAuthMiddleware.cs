using Microsoft.AspNetCore.Mvc;

namespace Knovara.Api.Middleware;

/// <summary>
/// Resolves the caller's tenant for <see cref="TenantHeader"/>.
///
/// When authentication is enabled the tenant comes only from the verified token claim, and overwrites
/// whatever the caller sent in the header — so controllers binding that header can only ever see the
/// authenticated tenant, and a request without a valid token is rejected here rather than reaching one.
/// When it is disabled the header is passed through untouched, which is the local-development path.
/// </summary>
public sealed class TenantAuthMiddleware(RequestDelegate next, TenantAuthOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!options.Enabled)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Authentication required.",
                "Send a bearer token. See README.md \"Authentication\".");
            return;
        }

        var tenantId = context.User.FindFirst(options.TenantClaim)?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "No tenant in token.",
                $"The token carries no '{options.TenantClaim}' claim, so no tenant can be derived from it.");
            return;
        }

        context.Request.Headers[TenantHeader.Name] = tenantId;
        await next(context);
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = statusCode, Title = title, Detail = detail });
    }
}
