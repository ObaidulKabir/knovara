using Knovara.ControlPlane.Data;

namespace Knovara.ControlPlane.Services;

public static class Bootstrap
{
    /// <summary>
    /// A brand-new deployment has no way to sign in, so the first platform account comes from configuration
    /// (Bootstrap:AdminEmail / Bootstrap:AdminPassword, ideally environment variables). It is created only
    /// when no accounts exist, so leaving the settings in place later does nothing.
    /// </summary>
    public static async Task EnsureFirstAdminAsync(IControlPlaneStore store, AccountService accounts, BootstrapOptions options, ILogger logger)
    {
        if (await store.CountAccountsAsync() > 0) return;

        if (string.IsNullOrWhiteSpace(options.AdminEmail) || string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            logger.LogWarning(
                "No accounts exist, so nobody can sign in. Set Bootstrap__AdminEmail and Bootstrap__AdminPassword "
                + "(at least {Min} characters) and restart to create the first platform administrator.", AccountService.MinPasswordLength);
            return;
        }

        var outcome = await accounts.CreateAsync(options.AdminEmail, options.AdminPassword, Roles.Platform, tenantId: null);
        if (outcome == CreateAccountOutcome.Created)
        {
            logger.LogInformation("Created the first platform administrator {Email}. Remove the Bootstrap settings now.", options.AdminEmail);
        }
        else
        {
            logger.LogError("Could not create the bootstrap administrator: {Outcome}.", outcome);
        }
    }
}
