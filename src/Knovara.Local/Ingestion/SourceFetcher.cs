using System.Net;
using System.Net.Sockets;
using Knovara.Core.Contracts;
using Knovara.Core.Models;

namespace Knovara.Local.Ingestion;

/// <summary>The source cannot be fetched or is not a supported document. Recorded on the resource, not retried.</summary>
public sealed class SourceException(string message) : Exception(message);

public sealed record FetchedSource(byte[] Data, string Suffix, string Name);

/// <summary>
/// Gets the bytes behind an ingestion job: an uploaded document from the store, a web page over http(s), or a file under
/// the configured source folder. Absolute paths are never read by the runtime; a local file is added by uploading its
/// content (the CLI and the desktop app do that), so the runtime cannot be asked to read arbitrary files on the machine.
/// </summary>
public sealed class SourceFetcher(IKnowledgeStore store, LocalOptions options, HttpClient http)
{
    public static readonly string[] Supported = [".md", ".markdown", ".txt", ".html", ".htm"];
    private const int MaxRedirects = 5;

    public async Task<FetchedSource> FetchAsync(IngestionJob job, CancellationToken ct)
    {
        FetchedSource source;
        if (UploadUri.IsUpload(job.Uri))
        {
            var name = job.Uri[UploadUri.Prefix.Length..];
            var upload = await store.GetUploadAsync(job.TenantId, job.ResourceId, ct)
                         ?? throw new SourceException("the uploaded content is no longer stored (the resource was deleted or never uploaded)");
            source = new FetchedSource(upload.Content, SuffixOf(name), name);
        }
        else if (Uri.TryCreate(job.Uri, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            source = await FetchUrlAsync(url, ct);
        }
        else
        {
            source = ReadFromSourceRoot(job.Uri);
        }

        if (source.Data.Length > options.MaxSourceBytes) throw new SourceException($"'{source.Name}' is larger than {options.MaxSourceBytes} bytes");
        if (!Supported.Contains(source.Suffix))
        {
            throw new SourceException($"'{(source.Suffix.Length == 0 ? "no extension" : source.Suffix)}' is not supported by the built-in extractors "
                                      + $"({string.Join(", ", Supported)}). PDF, Word and PowerPoint need the Docling worker.");
        }
        return source;
    }

    private FetchedSource ReadFromSourceRoot(string relative)
    {
        if (string.IsNullOrWhiteSpace(options.SourceRoot))
            throw new SourceException("this runtime has no source folder; add the file by uploading its content instead");

        var root = Path.GetFullPath(options.SourceRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SourceException($"'{relative}' is outside the source folder");
        if (!File.Exists(candidate)) throw new SourceException($"'{relative}' was not found in the source folder");

        return new FetchedSource(File.ReadAllBytes(candidate), SuffixOf(candidate), Path.GetFileName(candidate));
    }

    private async Task<FetchedSource> FetchUrlAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsurePublicHostAsync(url, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = new Uri(url, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SourceException($"GET {url} returned {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > options.MaxSourceBytes) throw new SourceException($"{url} is larger than {options.MaxSourceBytes} bytes");
            }

            var suffix = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
            if (!Supported.Contains(suffix))
            {
                suffix = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
                {
                    "text/html" or "application/xhtml+xml" => ".html",
                    "text/markdown" => ".md",
                    "text/plain" => ".md",
                    var other => throw new SourceException($"unsupported content type '{other ?? "unknown"}' at {url}")
                };
            }
            var name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
            return new FetchedSource(buffer.ToArray(), suffix, name.Length > 0 ? name : url.Host);
        }
        throw new SourceException($"more than {MaxRedirects} redirects");
    }

    /// <summary>Refuses hosts that resolve to private, loopback or link-local addresses (internal services, cloud metadata).</summary>
    private async Task EnsurePublicHostAsync(Uri url, CancellationToken ct)
    {
        if (options.AllowPrivateUrls) return;
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(url.Host, ct); }
        catch (SocketException ex) { throw new SourceException($"cannot resolve {url.Host}: {ex.Message}"); }

        foreach (var address in addresses)
        {
            if (!IsGlobal(address)) throw new SourceException($"refusing {url.Host}: it resolves to non-public address {address}");
        }
    }

    internal static bool IsGlobal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || b[0] >= 224);
        }

        var v6 = ip.GetAddressBytes();
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (v6[0] & 0xFE) == 0xFC);
    }

    private static string SuffixOf(string name) => Path.GetExtension(name).ToLowerInvariant();
}
