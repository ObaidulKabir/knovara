using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knovara.Cli;

// Mirrors of the API's response shapes. The CLI deliberately does not reference Knovara.Core: it
// talks the same public HTTP contract any external caller would, the way an SDK consumer does.
public sealed record ResourceReference(string ResourceId, string Type, string Uri, string? SourceVersion, string TenantId, string Status, string? Error = null);
public sealed record EvidenceItem(string NodeId, string ResourceId, string Title, string Content, string SourceVersion, string RetrievedVia, double RelevanceScore);
public sealed record ContextPackage(string QueryId, IReadOnlyList<EvidenceItem> Evidence, int EstimatedTokenCount);
public sealed record RetrievalResult(string QueryId, IReadOnlyList<EvidenceItem> Evidence, bool EvidenceSufficient, string? FollowUpSearchHint, bool Reranked);

public sealed class ApiException(string message) : Exception(message);

/// <summary>Thin HTTP client for the Knovara API. Every call carries the tenant header, as any caller must.</summary>
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _http;

    public ApiClient(string baseUrl, string tenantId)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
    }

    public Task<ResourceReference> IngestAsync(string uri, string type, CancellationToken ct) =>
        PostAsync<ResourceReference>("api/resources/ingest", new { uri, type }, ct);

    /// <summary>Uploads a local file's content, so the server (which may be on another machine, or just the local
    /// runtime, which has no source folder by default) never needs to read this path itself.</summary>
    public async Task<ResourceReference> UploadAsync(string filePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        return await PostAsync<ResourceReference>("api/resources/upload",
            new { fileName = Path.GetFileName(filePath), contentBase64 = Convert.ToBase64String(bytes) }, ct);
    }

    public async Task<ResourceReference?> GetResourceAsync(string resourceId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/resources/{resourceId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ResourceReference>(JsonOptions, ct);
    }

    public Task<ContextPackage> AskAsync(string sessionId, string question, int tokenBudget, CancellationToken ct) =>
        PostAsync<ContextPackage>("api/retrieval/ask", new { sessionId, question, tokenBudget }, ct);

    public Task<RetrievalResult> SearchAsync(string question, string mode, int topK, CancellationToken ct) =>
        PostAsync<RetrievalResult>("api/retrieval/search", new { question, mode, topK }, ct);

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new ApiException($"{path} returned an empty body.");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ApiException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
