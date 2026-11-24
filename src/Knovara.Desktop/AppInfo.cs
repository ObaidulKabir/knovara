using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Knovara.Common.Device;

namespace Knovara.Desktop
{
    /// <summary>What this installed copy of the app is, as reported to the control plane at each check-in.</summary>
    public static class AppInfo
    {
        /// <summary>The version stamped into the build (see Directory/csproj Version), without any "+commit" suffix.</summary>
        public static string Version { get; } = ReadVersion();

        public static string Os { get; } = RuntimeInformation.OSDescription;

        private static string ReadVersion()
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (info ?? "0.0.0").Split('+')[0];
        }

        /// <summary>True if <paramref name="latest"/> (e.g. "1.2.0") is newer than this build. Pre-release suffixes are ignored.</summary>
        public static bool IsNewerThanThisBuild(string? latest)
            => TryParse(latest, out var l) && TryParse(Version, out var current) && l > current;

        private static bool TryParse(string? text, out Version version)
        {
            version = new Version();
            return text is not null && System.Version.TryParse(text.Split('-')[0], out version!);
        }
    }

    /// <summary>Windows DPAPI, bound to the signed-in Windows user: another user, or another PC, cannot decrypt the device secret.</summary>
    public sealed class DpapiProtector : ISecretProtector
    {
        // Not a secret: it only ties the blob to this application, so another app's DPAPI data is not accepted here.
        private static readonly byte[] Entropy = "knovara-device-secret-v1"u8.ToArray();

        public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}
