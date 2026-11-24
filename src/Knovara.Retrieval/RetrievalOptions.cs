namespace Knovara.Retrieval;

public sealed class RetrievalOptions
{
    /// <summary>Adaptive reranks when the top two semantic cosine scores differ by less than this; 0.038 kept R@3/MRR within 0.01 of always reranking on eval/questions.json.</summary>
    public double AdaptiveMarginThreshold { get; init; } = 0.038;
}
