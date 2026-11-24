using System.Text.Json;
using System.Text.Json.Serialization;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using RabbitMQ.Client;

namespace Knovara.Ingestion;

public sealed class RabbitMqOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string VirtualHost { get; init; } = "/";
}

/// <summary>Publishes ingestion jobs to a durable queue consumed by workers/document-processing.</summary>
public sealed class RabbitMqIngestionQueue(RabbitMqOptions options) : IIngestionQueue, IAsyncDisposable
{
    public const string QueueName = "ingestion.requests";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnection? _connection;

    public async Task EnqueueAsync(IngestionJob job, CancellationToken ct = default)
    {
        var connection = await GetConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: ct);

        var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: QueueName,
            mandatory: true,
            basicProperties: properties,
            body: JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions),
            cancellationToken: ct);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            if (_connection is not null) await _connection.DisposeAsync();

            var factory = new ConnectionFactory
            {
                HostName = options.Host,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost
            };
            _connection = await factory.CreateConnectionAsync("knovara-api", ct);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}
