using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace Knovara.Mcp;

// Mirrors of the API's JSON responses. Like the CLI, this talks the same public HTTP contract any external
// caller would, rather than referencing Knovara.Core.
public sealed record ResourceReference(string ResourceId, string Type, string Uri, string? SourceVersion, string TenantId, string Status, string? Error = null);
public sealed record ResourcePage(IReadOnlyList<ResourceReference> Items, int Total);
public sealed record ResourceStats(int Resources, Dictionary<string, int> ByStatus, Dictionary<string, int> ByType, int Passages, int EmbeddedPassages, long ContentCharacters, long UploadedBytes);
public sealed record PassageSummary(string NodeId, string Level, string Title, string? Content);
public sealed record PassagePage(IReadOnlyList<PassageSummary> Items, int Total);
public sealed record BulkDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> NotDeleted);
public sealed record EvidenceItem(string NodeId, string ResourceId, string Title, string Content, string SourceVersion, string RetrievedVia, double RelevanceScore);
public sealed record ContextPackage(string QueryId, IReadOnlyList<EvidenceItem> Evidence, int EstimatedTokenCount);
public sealed record RetrievalResult(string QueryId, IReadOnlyList<EvidenceItem> Evidence, bool EvidenceSufficient, string? FollowUpSearchHint, bool Reranked);

/// <summary>
/// HTTP client for the Knovara API, fixed to ONE tenant taken from the server's configuration. The tenant is
/// deliberately not a tool argument: a model (or a prompt injected into a document it read) must not be able
/// to widen its own scope to another tenant's data.
/// </summary>
public sealed class KnovaraApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public KnovaraApiClient(HttpClient http) => _http = http;

    public static HttpClient CreateHttpClient(string baseUrl, string tenantId, string? jwt)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(180) };
        http.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (!string.IsNullOrWhiteSpace(jwt))
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        }
        return http;
    }

    public Task<ContextPackage> AskAsync(string sessionId, string question, int tokenBudget, CancellationToken ct) =>
        PostAsync<ContextPackage>("api/retrieval/ask", new { sessionId, question, tokenBudget }, ct);

    public Task<RetrievalResult> SearchAsync(string question, string mode, int topK, CancellationToken ct) =>
        PostAsync<RetrievalResult>("api/retrieval/search", new { question, mode, topK }, ct);

    public Task<ResourceReference> IngestAsync(string uri, string type, CancellationToken ct) =>
        PostAsync<ResourceReference>("api/resources/ingest", new { uri, type }, ct);

    public async Task<ResourcePage> ListAsync(string? status, string? type, string? search, int limit, int offset, CancellationToken ct)
    {
        var query = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (status is not null) query.Add($"status={Uri.EscapeDataString(status)}");
        if (type is not null) query.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"q={Uri.EscapeDataString(search)}");

        using var response = await SendAsync(() => _http.GetAsync("api/resources?" + string.Join("&", query), ct));
        await ThrowIfFailedAsync(response, ct);
        var items = await response.Content.ReadFromJsonAsync<List<ResourceReference>>(JsonOptions, ct) ?? [];
        var total = response.Headers.TryGetValues("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var t) ? t : items.Count;
        return new ResourcePage(items, total);
    }

    public Task<ResourceStats> StatsAsync(CancellationToken ct) => GetJsonAsync<ResourceStats>("api/resources/stats", ct);

    public async Task<PassagePage?> PassagesAsync(string resourceId, int limit, int offset, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync(
            $"api/resources/{Uri.EscapeDataString(resourceId)}/passages?limit={limit}&offset={offset}", ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<PassagePage>(JsonOptions, ct);
    }

    public Task<ResourceReference> UploadAsync(string fileName, string content, CancellationToken ct) =>
        PostAsync<ResourceReference>("api/resources/upload", new { fileName, content }, ct);

    public async Task<ResourceReference?> ReingestAsync(string resourceId, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsync($"api/resources/{Uri.EscapeDataString(resourceId)}/reingest", null, ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ResourceReference>(JsonOptions, ct);
    }

    public Task<BulkDeleteResult> BulkDeleteAsync(IReadOnlyList<string>? resourceIds, string? status, CancellationToken ct) =>
        PostAsync<BulkDeleteResult>("api/resources/delete", new { resourceIds, status }, ct);

    public async Task<ResourceReference?> GetAsync(string resourceId, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync($"api/resources/{Uri.EscapeDataString(resourceId)}", ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ResourceReference>(JsonOptions, ct);
    }

    /// <returns>true if deleted, false if it did not exist for this tenant.</returns>
    public async Task<bool> DeleteAsync(string resourceId, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.DeleteAsync($"api/resources/{Uri.EscapeDataString(resourceId)}", ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await ThrowIfFailedAsync(response, ct);
        return true;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.GetAsync(path, ct));
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new McpException($"{path} returned an empty response.");
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(() => _http.PostAsJsonAsync(path, body, JsonOptions, ct));
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new McpException($"{path} returned an empty response.");
    }

    // Connection failures and timeouts become a message the model can relay, not a protocol-level crash.
    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Could not reach the Knovara API at {_http.BaseAddress}: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new McpException("The Knovara API did not respond in time.");
        }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "unauthorized - the server's KNOVARA_JWT is missing or invalid",
            HttpStatusCode.Forbidden => "forbidden - the token has no tenant claim",
            _ => body.Length <= 400 ? body : body[..400] + "…"
        };
        throw new McpException($"Knovara API error {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }
}
