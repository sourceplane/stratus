using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;

namespace Stratus.Messaging;

// The Azure consume-side transports, moved here from the projector's and the
// notifier's Infrastructure projects.
//
// They live beside EventHubPublisher and ServiceBusCommandSender because this
// is already the only project that references the Azure SDKs — one place in
// the solution knows what a broker is, and adding a second target means adding
// a file here rather than editing a service.

/// <summary>
/// Event Hubs over its Kafka wire-protocol endpoint — retention, replay,
/// consumer groups. Read models are rebuildable from the log, which is what
/// makes one safe to throw away and recompute.
/// </summary>
public sealed class EventHubsEventTransport(
    IConfiguration configuration,
    ILogger<EventHubsEventTransport> logger) : IEventTransport
{
    public async Task ConsumeAsync(
        Func<IntegrationEvent, CancellationToken, Task> handle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var ns = configuration["Messaging:EventHubsNamespace"];
        var hub = configuration["Messaging:EventHubName"];
        var group = configuration["Messaging:ConsumerGroup"]
                    ?? EventHubConsumerClient.DefaultConsumerGroupName;

        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(hub))
        {
            AzureTransportLog.EventsNotConfigured(logger);
            return;
        }

        await using var consumer = new EventHubConsumerClient(
            group, $"{ns}.servicebus.windows.net", hub, new DefaultAzureCredential());

        await foreach (var partitionEvent in consumer.ReadEventsAsync(ct).ConfigureAwait(false))
        {
            if (partitionEvent.Data is null)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(partitionEvent.Data.EventBody.ToArray());
            var @event = JsonSerializer.Deserialize<IntegrationEvent>(json);
            if (@event is null)
            {
                AzureTransportLog.UnparseableEvent(logger, partitionEvent.Data.OffsetString);
                continue;
            }

            await handle(@event, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Service Bus over AMQP 1.0 — queues, sessions, scheduled delivery and a
/// native dead-letter queue. The RabbitMQ role.
/// </summary>
public sealed class ServiceBusCommandTransport(
    IConfiguration configuration,
    ILogger<ServiceBusCommandTransport> logger) : ICommandTransport
{
    public async Task ConsumeAsync(
        Func<IntegrationCommand, CancellationToken, Task<CommandOutcome>> handle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var ns = configuration["Messaging:ServiceBusNamespace"];
        var queue = configuration["Messaging:CommandQueueName"];

        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(queue))
        {
            // Idle rather than crash-looping: an unconfigured broker is a
            // deployment state, and a restart loop hides it.
            AzureTransportLog.CommandsNotConfigured(logger);
            return;
        }

        await using var client = new ServiceBusClient(
            $"{ns}.servicebus.windows.net", new DefaultAzureCredential());
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4,
        });

        processor.ProcessMessageAsync += async args =>
        {
            var command = JsonSerializer.Deserialize<IntegrationCommand>(args.Message.Body.ToString());
            if (command is null)
            {
                await args.DeadLetterMessageAsync(
                    args.Message, "unparseable", "Body is not an IntegrationCommand").ConfigureAwait(false);
                return;
            }

            var outcome = await handle(command, args.CancellationToken).ConfigureAwait(false);
            if (!outcome.Completed)
            {
                await args.DeadLetterMessageAsync(
                    args.Message, outcome.ReasonCode, outcome.ReasonMessage).ConfigureAwait(false);
                return;
            }

            await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
        };

        processor.ProcessErrorAsync += error =>
        {
            AzureTransportLog.ProcessorError(logger, error.ErrorSource, error.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(ct).ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// Source-generated logging. The processor-error handler is called from the
/// Service Bus SDK's own callback, potentially at high frequency during a
/// broker outage — exactly when the logging path must not allocate.
/// </summary>
internal static partial class AzureTransportLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Event Hubs is not configured; the event consumer is idle.")]
    public static partial void EventsNotConfigured(ILogger logger);

    // OffsetString, not Offset: the numeric Offset is [Obsolete] as of
    // Azure.Messaging.EventHubs 5.12 — offsets are not numeric on every
    // namespace — and an obsolete member is an error under
    // TreatWarningsAsErrors. It is nullable when the event was not read from a
    // partition, which cannot happen on this path but is typed so.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unparseable event at offset {Offset}.")]
    public static partial void UnparseableEvent(ILogger logger, string? offset);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Service Bus is not configured; the command consumer is idle.")]
    public static partial void CommandsNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processor error in {Source}.")]
    public static partial void ProcessorError(
        ILogger logger,
        ServiceBusErrorSource source,
        Exception exception);
}
