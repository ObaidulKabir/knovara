namespace Knovara.Core.Contracts;

public record TaskMetrics(
    string TaskId,
    int InputTokens,
    double LatencyMs,
    int LlmCallCount,
    double? RecallAtK,
    bool? AnswerAccurate,
    bool? CitationAccurate,
    bool CacheHit,
    bool Reprocessed);

/// <summary>Records per-task metrics so Knovara can be benchmarked against baseline/standard-RAG/advanced-RAG.</summary>
public interface IEvaluationService
{
    Task RecordAsync(TaskMetrics metrics, CancellationToken ct = default);

    Task<IReadOnlyList<TaskMetrics>> GetRecentAsync(int count, CancellationToken ct = default);
}
