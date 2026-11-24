using System.Text.Json;

namespace Knovara.Common.Device;

/// <summary>Protects the device secret at rest. The desktop supplies Windows DPAPI (bound to the signed-in Windows user).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Throws if the data was protected for a different user or machine, or is corrupt.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Remembers that this PC is enrolled. Everything is stored in plain JSON except the device secret, which only
/// ever touches disk in protected form, so copying the file to another user or computer does not carry the identity along.
/// </summary>
public sealed class DeviceStore(string path, ISecretProtector protector)
{
    private sealed record Stored(string ControlPlaneUrl, string DeviceId, string TenantId, string ApiBaseUrl, string ProtectedSecret);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Knovara", "device.json");

    public void Save(DeviceIdentity identity)
    {
        var stored = new Stored(
            identity.ControlPlaneUrl, identity.DeviceId, identity.TenantId, identity.ApiBaseUrl,
            Convert.ToBase64String(protector.Protect(System.Text.Encoding.UTF8.GetBytes(identity.DeviceSecret))));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Write beside the target and move into place, so a crash cannot leave a half-written identity.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The saved identity, or null if there is none or it cannot be read (missing, corrupt, or saved by a different Windows user).</summary>
    public DeviceIdentity? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
            if (stored is null) return null;

            var secret = System.Text.Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored.ProtectedSecret)));
            return new DeviceIdentity(stored.ControlPlaneUrl, stored.DeviceId, secret, stored.TenantId, stored.ApiBaseUrl);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
