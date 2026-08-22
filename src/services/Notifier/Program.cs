using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

// The command side: Service Bus over AMQP 1.0 — queues, sessions, scheduled
// delivery and a native dead-letter queue. The RabbitMQ role.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("notifier");
builder.Services.AddDbContext<StratusDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddHostedService<CommandConsumer>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

internal sealed class CommandConsumer(
    IConfiguration config,
    IServiceScopeFactory scopes,
    ILogger<CommandConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = config["Messaging:ServiceBusNamespace"];
        var queue = config["Messaging:CommandQueueName"];
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(queue))
        {
            logger.LogWarning("Service Bus is not configured; the consumer is idle.");
            return;
        }

        // Passwordless: managed identity, no connection string with an embedded key.
        await using var client = new ServiceBusClient($"{ns}.servicebus.windows.net", new DefaultAzureCredential());
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4,
        });

        processor.ProcessMessageAsync += async eventArgs =>
        {
            var body = eventArgs.Message.Body.ToString();
            var command = JsonSerializer.Deserialize<CommandEnvelope>(body);
            if (command is null)
            {
                // Unparseable: dead-letter rather than retry forever.
                await eventArgs.DeadLetterMessageAsync(eventArgs.Message, "unparseable", "Body is not a CommandEnvelope");
                return;
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StratusDbContext>();

            var handled = await Idempotency.OnceAsync(db, command.CommandId, "notifier", () =>
            {
                logger.LogInformation("Delivering {Type} for tenant {TenantId}.", command.Type, command.TenantId);
                return Task.CompletedTask;
            }, eventArgs.CancellationToken);

            if (!handled)
            {
                logger.LogDebug("Command {CommandId} already handled; skipping.", command.CommandId);
            }

            await eventArgs.CompleteMessageAsync(eventArgs.Message, eventArgs.CancellationToken);
        };

        processor.ProcessErrorAsync += error =>
        {
            logger.LogError(error.Exception, "Service Bus processor error in {Source}.", error.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
