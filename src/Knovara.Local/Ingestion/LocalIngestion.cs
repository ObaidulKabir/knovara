using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Knovara.Core.Contracts;
using Knovara.Core.Ingestion;
using Knovara.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knovara.Local.Ingestion;

/// <summary>The local runtime's counterpart of the RabbitMQ queue: jobs wait in memory for the in-process worker.</summary>
public sealed class InProcessIngestionQueue : IIngestionQueue
{
    private readonly Channel<IngestionJob> _jobs = Channel.CreateUnbounded<IngestionJob>(new UnboundedChannelOptions { SingleReader = true });

    public Task EnqueueAsync(IngestionJob job, CancellationToken ct = default) => _jobs.Writer.WriteAsync(job, ct).AsTask();

    internal IAsyncEnumerable<IngestionJob> ReadAllAsync(CancellationToken ct) => _jobs.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Runs ingestion jobs one at a time inside the runtime: fetch, hash, skip if unchanged, extract with the built-in extractors,
/// then commit through the same ExtractionCommitter the API endpoint uses. One job at a time keeps memory flat.
/// </summary>
public sealed class LocalIngestionWorker(
    InProcessIngestionQueue queue,
    IKnowledgeStore store,
    IModelService models,
    ExtractionCommitter committer,
    SourceFetcher fetcher,
    ILogger<LocalIngestionWorker> logger) : BackgroundService
{
    // Part of every version hash: the stored hierarchy depends on the extraction logic as well as on the source bytes,
    // so bumping this re-processes every source once on its next ingest.
    internal const string ExtractorVersion = "local-hierarchy-1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad job must not stop the worker; the resource shows why it failed.
                logger.LogError(ex, "Unexpected failure ingesting {Uri}", job.Uri);
                await store.MarkFailedAsync(job.TenantId, job.ResourceId, "Unexpected error: " + ex.Message, CancellationToken.None);
            }
        }
    }

    internal async Task ProcessAsync(IngestionJob job, CancellationToken ct)
    {
        try
        {
            var source = await fetcher.FetchAsync(job, ct);
            var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ExtractorVersion + "\0").Concat(source.Data).ToArray()));

            var model = await models.GetInfoAsync(ct);
            switch (await store.CheckVersionAsync(job.TenantId, job.ResourceId, hash, model.EmbeddingModel, ct))
            {
                case VersionCheck.NotFound:
                    return;     // deleted while queued
                case VersionCheck.Unchanged:
                    logger.LogInformation("{Uri} unchanged, skipped", job.Uri);
                    return;
            }

            var started = DateTime.UtcNow;
            var nodes = Extract(source);
            var outcome = await committer.CommitAsync(job.TenantId, job.ResourceId, hash, nodes, ct);
            if (outcome.Status == ExtractionStatus.Committed)
                logger.LogInformation("{Uri} -> {Nodes} nodes in {Seconds:0.0}s", job.Uri, nodes.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (SourceException ex)
        {
            logger.LogWarning("{Uri} failed: {Error}", job.Uri, ex.Message);
            await store.MarkFailedAsync(job.TenantId, job.ResourceId, ex.Message, ct);
        }
    }

    private static IReadOnlyList<ExtractedNode> Extract(FetchedSource source)
    {
        var text = Encoding.UTF8.GetString(source.Data);
        return source.Suffix is ".html" or ".htm"
            ? HtmlExtractor.Extract(text, source.Name)
            : MarkdownExtractor.Extract(text, source.Name);
    }
}
