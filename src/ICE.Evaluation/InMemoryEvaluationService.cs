using System.Collections.Concurrent;
using ICE.Core.Contracts;

namespace ICE.Evaluation;

/// <summary>
/// Placeholder implementation: keeps a bounded in-memory ring of recent task metrics.
/// Replace with persistent storage + OpenTelemetry export — see system_architecture.md
/// "Evaluation &amp; Observability Service" and system_specifications.md "Evaluation Plan".
/// </summary>
public sealed class InMemoryEvaluationService : IEvaluationService
{
    private const int MaxRetained = 1000;
    private readonly ConcurrentQueue<TaskMetrics> _metrics = new();

    public Task RecordAsync(TaskMetrics metrics, CancellationToken ct = default)
    {
        _metrics.Enqueue(metrics);
        while (_metrics.Count > MaxRetained) _metrics.TryDequeue(out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskMetrics>> GetRecentAsync(int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TaskMetrics>>(_metrics.Reverse().Take(count).ToList());
}
