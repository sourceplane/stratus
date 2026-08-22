using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Azure.Messaging.ServiceBus;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>Publishes facts onto the durable log.</summary>
public interface IEventPublisher
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default);
}

/// <summary>Sends commands to their single owner.</summary>
public interface ICommandSender
{
    Task SendAsync(IntegrationCommand command, CancellationToken ct = default);
}

public sealed class EventHubPublisher(EventHubProducerClient producer) : IEventPublisher
{
    public async Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Tenant as partition key: per-tenant ordering holds, cross-tenant work
        // stays parallel, and a hot tenant appears as a hot partition rather
        // than as unexplained lag.
        var options = new CreateBatchOptions { PartitionKey = @event.TenantId.ToString() };
        using var batch = await producer.CreateBatchAsync(options, ct).ConfigureAwait(false);

        var data = new EventData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event)));
        data.Properties["type"] = @event.Type;
        data.Properties["eventId"] = @event.EventId.ToString();

        if (!batch.TryAdd(data))
        {
            throw new InvalidOperationException($"Event {@event.EventId} exceeds the maximum batch size.");
        }

        await producer.SendAsync(batch, ct).ConfigureAwait(false);
    }
}

public sealed class ServiceBusCommandSender(ServiceBusSender sender) : ICommandSender
{
    public Task SendAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = new ServiceBusMessage(JsonSerializer.Serialize(command))
        {
            // MessageId drives Service Bus duplicate detection; SessionId keeps
            // a tenant ordered without serialising every tenant.
            MessageId = command.CommandId.ToString(),
            SessionId = command.TenantId.ToString(),
            Subject = command.Type,
        };

        return sender.SendMessageAsync(message, ct);
    }
}
