using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Knovara.Common.Runtime;

/// <summary>
/// Starts the installed local runtime (the API in <c>Runtime:Mode=Local</c>) on demand, so the CLI and the MCP server
/// work right after installing, with nothing to run or configure first. Used only for a loopback API URL: a remote or
/// team server is never something this process should try to start.
/// </summary>
public static class LocalRuntimeLauncher
{
    private const int StartupTimeoutSeconds = 25;
    private const int ProbeTimeoutMilliseconds = 400;

    /// <summary>
    /// If <paramref name="apiUrl"/> is loopback and nothing answers there yet, starts the runtime detached and waits for
    /// it to accept connections. Does nothing for a non-loopback URL. Never throws: when the runtime cannot be found or
    /// started (for instance, a developer checkout with no installed layout), callers fall back to their normal
    /// "could not reach the API" error, with <paramref name="warn"/> given the reason first.
    /// </summary>
    public static async Task EnsureRunningAsync(string apiUrl, Action<string>? warn = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) || !IsLoopback(uri.Host)) return;
        if (await IsListeningAsync(uri.Host, uri.Port, ct)) return;

        var exe = FindRuntimeExecutable();
        if (exe is null) return;

        try
        {
            Start(exe, uri.Port);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"could not start the local runtime at '{exe}': {ex.Message}");
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsListeningAsync(uri.Host, uri.Port, ct)) return;
            await Task.Delay(300, ct);
        }
        warn?.Invoke($"started the local runtime but it did not answer on port {uri.Port} within {StartupTimeoutSeconds}s; see "
                     + Path.Combine(DataDirectory(), "runtime.log"));
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static async Task<bool> IsListeningAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds), ct);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The installer lays out <c>{app}\runtime\Knovara.Api.exe</c> beside <c>{app}\cli\knovara.exe</c> and
    /// <c>{app}\mcp\knovara-mcp.exe</c>, so from either sibling's own folder the runtime is "..\runtime\Knovara.Api.exe".
    /// KNOVARA_RUNTIME_EXE overrides this for a non-standard layout or a developer checkout.
    /// </summary>
    private static string? FindRuntimeExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("KNOVARA_RUNTIME_EXE");
        if (overridePath is { Length: > 0 }) return File.Exists(overridePath) ? overridePath : null;

        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime", "Knovara.Api.exe"));
        return File.Exists(candidate) ? candidate : null;
    }

    private static string DataDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Knovara");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Start(string exe, int port)
    {
        var dataDir = DataDirectory();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        // No stdio redirection: the handles would need this process to keep pumping them, but the runtime
        // must keep running after the CLI command (or the MCP server) exits. It logs to a file instead.
        psi.Environment["Runtime__Mode"] = "Local";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["Local__DatabasePath"] = Path.Combine(dataDir, "knovara.db");
        psi.Environment["Local__LogFile"] = Path.Combine(dataDir, "runtime.log");

        Process.Start(psi);
    }
}
