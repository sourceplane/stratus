using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;
using Stratus.Notifier.Application;

namespace Stratus.Notifier.Infrastructure;

/// <summary>
/// The command side: Service Bus over AMQP 1.0 — queues, sessions, scheduled
/// delivery and a native dead-letter queue. The RabbitMQ role.
/// </summary>
public sealed class CommandConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopes,
    ILogger<CommandConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = configuration["Messaging:ServiceBusNamespace"];
        var queue = configuration["Messaging:CommandQueueName"];

        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(queue))
        {
            // Idle rather than crash-looping: an unconfigured broker is a
            // deployment state, and a restart loop hides it.
            CommandConsumerLog.NotConfigured(logger);
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

            using var scope = scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<INotificationHandler>();
            var result = await handler.HandleAsync(command, args.CancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                await args.DeadLetterMessageAsync(
                    args.Message, result.Error.Code, result.Error.Message).ConfigureAwait(false);
                return;
            }

            await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
        };

        processor.ProcessErrorAsync += error =>
        {
            CommandConsumerLog.ProcessorError(logger, error.ErrorSource, error.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
        await Task.Delay(Timeout.Infinite, stoppingToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// Source-generated logging. The processor-error handler is called from the
/// Service Bus SDK's own callback, potentially at high frequency during a
/// broker outage — exactly when the logging path must not allocate.
/// </summary>
internal static partial class CommandConsumerLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Service Bus is not configured; the command consumer is idle.")]
    public static partial void NotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processor error in {Source}.")]
    public static partial void ProcessorError(
        ILogger logger,
        ServiceBusErrorSource source,
        Exception exception);
}
