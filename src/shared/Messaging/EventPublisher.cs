using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>Publishes domain events — facts — onto the durable log.</summary>
public interface IEventPublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default);
}

public sealed class EventHubPublisher(EventHubProducerClient producer) : IEventPublisher, IAsyncDisposable
{
    public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // The tenant is the partition key: per-tenant ordering is guaranteed,
        // cross-tenant work stays parallel, and a hot tenant shows up as a hot
        // partition rather than as unexplained lag.
        var options = new CreateBatchOptions { PartitionKey = envelope.TenantId.ToString() };
        using var batch = await producer.CreateBatchAsync(options, ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(envelope);
        var data = new EventData(Encoding.UTF8.GetBytes(json));
        data.Properties["type"] = envelope.Type;
        data.Properties["eventId"] = envelope.EventId.ToString();

        if (!batch.TryAdd(data))
        {
            throw new InvalidOperationException($"Event {envelope.EventId} exceeds the maximum batch size.");
        }

        await producer.SendAsync(batch, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => producer.DisposeAsync();
}
