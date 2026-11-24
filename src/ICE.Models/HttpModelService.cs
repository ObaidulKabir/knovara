using System.Net.Http.Json;
using ICE.Core.Contracts;

namespace ICE.Models;

public sealed class ModelServiceOptions
{
    public string BaseUrl { get; init; } = "http://localhost:8090";

    /// <summary>
    /// CPU inference on real (not synthetic-short) passage text is slow and varies a lot by content
    /// length; 60s proved too tight for some rerank calls during benchmarking. Raise further, or move
    /// to a faster model/host, before any user-facing deployment — a multi-second /ask is a UX problem
    /// even once it stops timing out outright.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>HTTP client for services/model-service. Passages are embedded in batches to bound request size.</summary>
public sealed class HttpModelService(HttpClient http) : IModelService
{
    private const int PassageBatchSize = 64;

    private ModelInfo? _info;

    public async Task<ModelInfo> GetInfoAsync(CancellationToken ct = default)
    {
        if (_info is not null) return _info;
        var health = await http.GetFromJsonAsync<HealthResponse>("health", ct)
                     ?? throw new InvalidOperationException("The model service returned no health information.");
        return _info = new ModelInfo(health.EmbeddingModel, health.Dimensions, health.RerankModel);
    }

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(PassageBatchSize))
        {
            vectors.AddRange(await EmbedAsync(batch, "passage", ct));
        }
        return vectors;
    }

    public async Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default)
        => (await EmbedAsync([question], "query", ct))[0];

    public async Task<IReadOnlyList<double>> RerankAsync(string question, IReadOnlyList<string> documents, CancellationToken ct = default)
    {
        if (documents.Count == 0) return [];
        using var response = await http.PostAsJsonAsync("rerank", new { query = question, documents }, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RerankResponse>(ct);
        return result?.Scores ?? throw new InvalidOperationException("The model service returned no rerank scores.");
    }

    private async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, string kind, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("embed", new { texts, kind }, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                     ?? throw new InvalidOperationException("The model service returned no embeddings.");
        if (result.Vectors.Length != texts.Count)
        {
            throw new InvalidOperationException($"Asked for {texts.Count} embeddings, got {result.Vectors.Length}.");
        }
        return result.Vectors;
    }

    private sealed record HealthResponse(string EmbeddingModel, int Dimensions, string RerankModel);
    private sealed record EmbedResponse(string Model, int Dimensions, float[][] Vectors);
    private sealed record RerankResponse(string Model, double[] Scores);
}
