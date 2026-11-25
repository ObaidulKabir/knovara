using Knovara.Core.Contracts;
using Knovara.Core.Ingestion;
using Knovara.Local.Ingestion;
using Knovara.Local.Models;
using Knovara.Local.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Knovara.Local;

public static class LocalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local runtime's implementations of the store, index, queue and model contracts (SQLite, FTS5, in-process
    /// queue and worker, in-process ONNX models), in place of the Postgres, RabbitMQ and model-service ones. Everything
    /// else (retrieval, context optimization, controllers) is shared with server mode.
    /// </summary>
    public static IServiceCollection AddKnovaraLocal(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Local").Get<LocalOptions>() ?? new LocalOptions();
        services.AddSingleton(options);

        services.AddSingleton(new SqliteDatabase(options.DatabasePath));
        services.AddSingleton<IKnowledgeStore, SqliteKnowledgeStore>();
        services.AddSingleton<IIndexManagementService, SqliteIndexManagementService>();

        var models = new OnnxModelOptions { Threads = options.ModelThreads };
        if (options.ModelDirectory is not null) models.ModelDirectory = options.ModelDirectory;
        services.AddSingleton(models);
        services.AddSingleton<IModelService, OnnxModelService>();

        services.AddSingleton<InProcessIngestionQueue>();
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<InProcessIngestionQueue>());
        services.AddSingleton<ExtractionCommitter>();
        services.AddHttpClient<SourceFetcher>(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHostedService<LocalIngestionWorker>();
        services.AddHostedService<SchemaInitializer>();
        return services;
    }

    /// <summary>Creates the SQLite schema before the runtime serves requests.</summary>
    private sealed class SchemaInitializer(SqliteDatabase db) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken ct) => db.InitializeAsync(ct);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
