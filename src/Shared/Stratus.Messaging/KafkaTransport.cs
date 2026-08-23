using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;

namespace Stratus.Messaging;

// The event backbone on self-hosted Kafka.
//
// This is a second IMPLEMENTATION, not a second messaging model. Event Hubs is
// reached over its Kafka wire-protocol endpoint, so the semantics the Azure
// transport relies on — partitioned ordering, retained log, independent
// consumer groups — are Kafka's semantics to begin with. What changes is the
// client library and how a connection is described.
//
// Two behaviours are carried over deliberately, because they are contracts the
// rest of the fleet already depends on:
//
//   * Tenant id is the PARTITION KEY. Per-tenant ordering holds, cross-tenant
//     work stays parallel, and a hot tenant shows up as a hot partition rather
//     than as unexplained lag.
//   * An unconfigured broker leaves the service IDLE rather than throwing. An
//     unconfigured deployment is a state to report; a crash loop hides it
//     behind restart noise.

/// <summary>Kafka producer for the durable event log.</summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, byte[]>? producer;

    public KafkaEventPublisher(IConfiguration configuration, ILogger<KafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var servers = configuration["Messaging:KafkaBootstrapServers"];
        if (string.IsNullOrWhiteSpace(servers))
        {
            KafkaLog.PublisherNotConfigured(logger);
            return;
        }

        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = servers,
            // Durability over latency: the outbox has already committed this
            // event to Postgres, so losing it in the broker would mean the
            // system's own record says it was published when it was not.
            Acks = Acks.All,
            EnableIdempotence = true,
        }).Build();
    }

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (producer is null)
        {
            return;
        }

        var message = new Message<string, byte[]>
        {
            Key = @event.TenantId.ToString(),
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event)),
            Headers = new Headers
            {
                { "type", Encoding.UTF8.GetBytes(@event.Type) },
                { "eventId", Encoding.UTF8.GetBytes(@event.EventId.ToString()) },
            },
        };

        await producer.ProduceAsync(TopicFor(@event), message, ct).ConfigureAwait(false);
    }

    // One topic for the whole event log, mirroring the single Event Hub the
    // Azure transport publishes to. Splitting by event type would break the
    // ordering guarantee the partition key exists to provide.
    private static string TopicFor(IntegrationEvent @event) => KafkaTopics.Events;

    public void Dispose()
    {
        // Flush before disposing: an un-flushed produce is an event the outbox
        // has already marked dispatched.
        producer?.Flush(TimeSpan.FromSeconds(10));
        producer?.Dispose();
    }
}

/// <summary>Kafka consumer for the durable event log.</summary>
public sealed class KafkaEventTransport(
    IConfiguration configuration,
    ILogger<KafkaEventTransport> logger) : IEventTransport
{
    public async Task ConsumeAsync(
        Func<IntegrationEvent, CancellationToken, Task> handle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var servers = configuration["Messaging:KafkaBootstrapServers"];
        var group = configuration["Messaging:ConsumerGroup"];

        if (string.IsNullOrWhiteSpace(servers) || string.IsNullOrWhiteSpace(group))
        {
            KafkaLog.ConsumerNotConfigured(logger);
            return;
        }

        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = servers,
            GroupId = group,
            // Manual commit, after the handler returns. Auto-commit would
            // acknowledge an offset before the projection was written, so a
            // crash mid-handle would silently skip the event — the read model
            // would be missing a fact with nothing to indicate it.
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(KafkaTopics.Events);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    // Consume() is synchronous and blocking; the short timeout
                    // keeps the cancellation token responsive without spinning.
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex)
                {
                    KafkaLog.ConsumeFailed(logger, ex);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(result.Message.Value);
                IntegrationEvent? @event = null;
                try
                {
                    @event = JsonSerializer.Deserialize<IntegrationEvent>(json);
                }
                catch (JsonException)
                {
                    // Fall through to the unparseable path.
                }

                if (@event is null)
                {
                    // Commit past it: a body that cannot be deserialized will
                    // never deserialize, and blocking the partition on it stops
                    // every later event for every tenant on that partition.
                    KafkaLog.UnparseableEvent(logger, result.Offset.Value);
                    consumer.Commit(result);
                    continue;
                }

                await handle(@event, ct).ConfigureAwait(false);
                consumer.Commit(result);
            }
        }
        finally
        {
            consumer.Close();
        }
    }
}

/// <summary>Topic names, in one place so producer and consumer cannot drift.</summary>
public static class KafkaTopics
{
    public const string Events = "stratus.events";
}

internal static partial class KafkaLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Kafka is not configured; events are not being published.")]
    public static partial void PublisherNotConfigured(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Kafka is not configured; the event consumer is idle.")]
    public static partial void ConsumerNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unparseable event at offset {Offset}.")]
    public static partial void UnparseableEvent(ILogger logger, long offset);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kafka consume failed.")]
    public static partial void ConsumeFailed(ILogger logger, Exception exception);
}
