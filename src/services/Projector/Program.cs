using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

// The event side: Event Hubs over its Kafka wire-protocol endpoint — retention,
// replay, consumer groups. Projections are rebuildable from the log, which is
// what makes a read model safe to throw away and recompute.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("projector");
builder.Services.AddDbContext<StratusDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddHostedService<EventProjector>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

internal sealed class EventProjector(
    IConfiguration config,
    IServiceScopeFactory scopes,
    ILogger<EventProjector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = config["Messaging:EventHubsNamespace"];
        var hub = config["Messaging:EventHubName"];
        var group = config["Messaging:ConsumerGroup"] ?? EventHubConsumerClient.DefaultConsumerGroupName;
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(hub))
        {
            logger.LogWarning("Event Hubs is not configured; the projector is idle.");
            return;
        }

        await using var consumer = new EventHubConsumerClient(
            group, $"{ns}.servicebus.windows.net", hub, new DefaultAzureCredential());

        await foreach (var partitionEvent in consumer.ReadEventsAsync(stoppingToken))
        {
            if (partitionEvent.Data is null)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(partitionEvent.Data.EventBody.ToArray());
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(json);
            if (envelope is null)
            {
                logger.LogWarning("Skipping unparseable event at offset {Offset}.", partitionEvent.Data.Offset);
                continue;
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StratusDbContext>();

            await Idempotency.OnceAsync(db, envelope.EventId, "projector", () =>
            {
                logger.LogInformation("Projecting {Type} for tenant {TenantId}.", envelope.Type, envelope.TenantId);
                return Task.CompletedTask;
            }, stoppingToken);
        }
    }
}
