using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;
using Stratus.Projector.Application;

namespace Stratus.Projector.Infrastructure;

/// <summary>
/// The event side: Event Hubs over its Kafka wire-protocol endpoint —
/// retention, replay, consumer groups. Read models are rebuildable from the
/// log, which is what makes one safe to throw away and recompute.
/// </summary>
public sealed class EventConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopes,
    ILogger<EventConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = configuration["Messaging:EventHubsNamespace"];
        var hub = configuration["Messaging:EventHubName"];
        var group = configuration["Messaging:ConsumerGroup"]
                    ?? EventHubConsumerClient.DefaultConsumerGroupName;

        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(hub))
        {
            EventConsumerLog.NotConfigured(logger);
            return;
        }

        await using var consumer = new EventHubConsumerClient(
            group, $"{ns}.servicebus.windows.net", hub, new DefaultAzureCredential());

        await foreach (var partitionEvent in consumer.ReadEventsAsync(stoppingToken).ConfigureAwait(false))
        {
            if (partitionEvent.Data is null)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(partitionEvent.Data.EventBody.ToArray());
            var @event = JsonSerializer.Deserialize<IntegrationEvent>(json);
            if (@event is null)
            {
                EventConsumerLog.UnparseableEvent(logger, partitionEvent.Data.OffsetString);
                continue;
            }

            using var scope = scopes.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IProjectionDispatcher>();
            await dispatcher.DispatchAsync(@event, stoppingToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Source-generated logging — see ProjectorLog for the reasoning. The
/// unparseable-event warning sits inside the consume loop, which is the
/// hottest path in the service.
/// </summary>
internal static partial class EventConsumerLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Event Hubs is not configured; the projector is idle.")]
    public static partial void NotConfigured(ILogger logger);

    // OffsetString, not Offset: the numeric Offset is [Obsolete] as of
    // Azure.Messaging.EventHubs 5.12 — offsets are not numeric on every
    // namespace — and an obsolete member is an error under
    // TreatWarningsAsErrors. It is nullable when the event was not read
    // from a partition, which cannot happen on this path but is typed so.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unparseable event at offset {Offset}.")]
    public static partial void UnparseableEvent(ILogger logger, string? offset);
}
