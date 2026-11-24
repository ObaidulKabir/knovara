using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Knovara.ControlPlane.Data;

namespace Knovara.ControlPlane.Services;

public enum AddReleaseOutcome { Added, InvalidVersion, InvalidFile, TooLarge, DuplicateVersion }
public sealed record AddReleaseResult(AddReleaseOutcome Outcome, Release? Release = null);

/// <summary>
/// Hosts the desktop installers on this server's disk. Files are stored under a generated folder with a
/// sanitized name, never at a path taken from the upload, and every release records its SHA-256 so a
/// downloader can verify what they received.
/// </summary>
public sealed partial class ReleaseService(IControlPlaneStore store, ReleaseOptions options, TimeProvider clock)
{
    private static readonly string[] AllowedExtensions = [".exe", ".msi", ".zip"];

    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    /// <summary>Just the file name, with anything but letters, digits, dot, dash and underscore replaced; null if it has no allowed extension.</summary>
    public static string? SafeFileName(string? name)
    {
        var leaf = (name ?? "").Replace('\\', '/').Split('/').Last().Trim();
        var cleaned = UnsafeChars().Replace(leaf, "_");
        if (cleaned.Length is 0 or > 120 || cleaned.StartsWith('.')) return null;
        return AllowedExtensions.Any(e => cleaned.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? cleaned : null;
    }

    public async Task<AddReleaseResult> AddAsync(
        Stream content, string? originalFileName, string? version, string? notes, string uploadedBy, CancellationToken ct = default)
    {
        if (!IsValidVersion(version)) return new AddReleaseResult(AddReleaseOutcome.InvalidVersion);
        var fileName = SafeFileName(originalFileName);
        if (fileName is null) return new AddReleaseResult(AddReleaseOutcome.InvalidFile);

        var releaseId = Guid.NewGuid().ToString("n");
        var folder = Path.Combine(options.Directory, releaseId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);

        try
        {
            long size = 0;
            var tooLarge = false;
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > options.MaxUploadBytes) { tooLarge = true; break; }
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            // Cleaned up only after the file handle above is closed; deleting an open file fails on Windows.
            if (tooLarge) { CleanUp(folder); return new AddReleaseResult(AddReleaseOutcome.TooLarge); }
            if (size == 0) { CleanUp(folder); return new AddReleaseResult(AddReleaseOutcome.InvalidFile); }

            var release = new Release(releaseId, version!, fileName, hash, size,
                string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), uploadedBy, clock.GetUtcNow().UtcDateTime, false);

            if (!await store.AddReleaseAsync(release, ct))
            {
                CleanUp(folder);
                return new AddReleaseResult(AddReleaseOutcome.DuplicateVersion);
            }

            // The first release becomes the current one, so a fresh install has something to offer.
            if (await store.GetCurrentReleaseAsync(ct) is null) await store.SetCurrentReleaseAsync(releaseId, ct);
            return new AddReleaseResult(AddReleaseOutcome.Added, release);
        }
        catch
        {
            CleanUp(folder);
            throw;
        }
    }

    public string PathFor(Release release) => Path.Combine(options.Directory, release.ReleaseId, release.FileName);

    public async Task<bool> DeleteAsync(string releaseId, CancellationToken ct = default)
    {
        var release = await store.GetReleaseAsync(releaseId, ct);
        if (release is null || !await store.DeleteReleaseAsync(releaseId, ct)) return false;
        CleanUp(Path.Combine(options.Directory, release.ReleaseId));
        return true;
    }

    private static void CleanUp(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless; the row is what matters */ }
    }

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}(-[0-9A-Za-z.-]{1,20})?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();
}
