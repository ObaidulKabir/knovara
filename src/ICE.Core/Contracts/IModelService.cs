namespace ICE.Core.Contracts;

/// <summary>The self-hosted models in use. Stored with every embedding so vectors from different models are never mixed.</summary>
public record ModelInfo(string EmbeddingModel, int Dimensions, string RerankModel);

/// <summary>Embeddings and reranking from the self-hosted model service (services/model-service).</summary>
public interface IModelService
{
    Task<ModelInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Embeds passages for storage, in the same order as <paramref name="texts"/>.</summary>
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default);

    /// <summary>Relevance of each document to the question (higher is more relevant), in input order.</summary>
    Task<IReadOnlyList<double>> RerankAsync(string question, IReadOnlyList<string> documents, CancellationToken ct = default);
}
