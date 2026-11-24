using System.Text.RegularExpressions;
using Knovara.ControlPlane.Data;
using Microsoft.AspNetCore.Identity;

namespace Knovara.ControlPlane.Services;

public enum CreateAccountOutcome { Created, EmailTaken, InvalidEmail, WeakPassword }
public enum SignInOutcome { Success, InvalidCredentials, LockedOut }
public sealed record SignInResult(SignInOutcome Outcome, Account? Account = null);

/// <summary>Local accounts: salted PBKDF2 hashes (ASP.NET Core's PasswordHasher), a minimum password length, and lockout after repeated failures.</summary>
public sealed partial class AccountService(IControlPlaneStore store, TimeProvider clock)
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly PasswordHasher<object> _hasher = new();

    // Verified against when the email is unknown, so "no such account" costs the same time as "wrong password".
    private readonly string _dummyHash = new PasswordHasher<object>().HashPassword(new object(), "not-a-real-password");

    public static string? PasswordProblem(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) return $"Use at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength) return $"Use at most {MaxPasswordLength} characters.";
        if (email is not null && password.Equals(email, StringComparison.OrdinalIgnoreCase)) return "The password must not be the email address.";
        return null;
    }

    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        return trimmed is { Length: > 0 and <= 254 } && EmailPattern().IsMatch(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public async Task<CreateAccountOutcome> CreateAsync(string? email, string? password, string role, string? tenantId, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return CreateAccountOutcome.InvalidEmail;
        if (PasswordProblem(password, normalized) is not null) return CreateAccountOutcome.WeakPassword;

        var created = await store.CreateAccountAsync(
            Guid.NewGuid().ToString("n"), normalized, _hasher.HashPassword(new object(), password!), role, tenantId, ct);
        return created ? CreateAccountOutcome.Created : CreateAccountOutcome.EmailTaken;
    }

    public async Task<SignInResult> SignInAsync(string? email, string? password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        var account = normalized is null ? null : await store.FindAccountByEmailAsync(normalized, ct);

        if (account is null || string.IsNullOrEmpty(password))
        {
            _hasher.VerifyHashedPassword(new object(), _dummyHash, password ?? "");
            return new SignInResult(SignInOutcome.InvalidCredentials);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (account.LockedUntil is { } until && until > now) return new SignInResult(SignInOutcome.LockedOut);

        var verdict = _hasher.VerifyHashedPassword(new object(), account.PasswordHash, password);
        if (verdict == PasswordVerificationResult.Failed)
        {
            await store.RecordLoginFailureAsync(account.AccountId, MaxFailedAttempts, LockoutDuration, ct);
            return new SignInResult(SignInOutcome.InvalidCredentials);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await store.SetPasswordHashAsync(account.AccountId, _hasher.HashPassword(new object(), password), ct);
        }
        if (account.FailedAttempts > 0 || account.LockedUntil is not null) await store.ResetLoginFailuresAsync(account.AccountId, ct);

        return new SignInResult(SignInOutcome.Success, account);
    }

    /// <returns>null on success, otherwise what to tell the user.</returns>
    public async Task<string?> ChangePasswordAsync(string accountId, string? current, string? replacement, CancellationToken ct = default)
    {
        var account = await store.GetAccountAsync(accountId, ct);
        if (account is null || string.IsNullOrEmpty(current)
            || _hasher.VerifyHashedPassword(new object(), account.PasswordHash, current) == PasswordVerificationResult.Failed)
        {
            return "The current password is wrong.";
        }

        var problem = PasswordProblem(replacement, account.Email);
        if (problem is not null) return problem;

        await store.SetPasswordHashAsync(accountId, _hasher.HashPassword(new object(), replacement!), ct);
        return null;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
