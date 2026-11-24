using Knovara.Core.Models;

namespace Knovara.Core.Contracts;

/// <summary>Hands resources to the out-of-process extraction worker.</summary>
public interface IIngestionQueue
{
    Task EnqueueAsync(IngestionJob job, CancellationToken ct = default);
}
