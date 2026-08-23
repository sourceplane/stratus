using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Stratus.Contracts;

namespace Stratus.Messaging;

// The command side on self-hosted RabbitMQ — the role Service Bus plays on the
// Azure lane. Both are AMQP, so this is the same programming model with a
// different client.
//
// ── Where the two brokers genuinely differ ──
//
// Service Bus has a NATIVE dead-letter queue: `DeadLetterMessageAsync` takes a
// reason and a description and the broker files the message away with them.
// RabbitMQ has no such thing — it has a dead-letter EXCHANGE, which the queue
// must be declared with, and a `BasicNack(requeue: false)` routes there. The
// reason has nowhere to live in the protocol, so it goes in a header.
//
// That difference is exactly why ICommandTransport takes a delegate returning
// CommandOutcome rather than exposing a message object: a service that had to
// know which settlement API it was holding would be a service coupled to a
// broker, which is the thing this seam exists to prevent.

/// <summary>RabbitMQ publisher for commands to their single owner.</summary>
public sealed class RabbitMqCommandSender(
    IConfiguration configuration,
    ILogger<RabbitMqCommandSender> logger) : ICommandSender
{
    public async Task SendAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var uri = configuration["Messaging:RabbitMqUri"];
        var queue = configuration["Messaging:CommandQueueName"];
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(queue))
        {
            RabbitMqLog.SenderNotConfigured(logger);
            return;
        }

        var factory = new ConnectionFactory { Uri = new Uri(uri) };
        await using var connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        await DeclareAsync(channel, queue, ct).ConfigureAwait(false);

        var properties = new BasicProperties
        {
            // MessageId is what an idempotent consumer keys on, mirroring the
            // Service Bus duplicate-detection id.
            MessageId = command.CommandId.ToString(),
            // No sessions in AMQP 0-9-1. The tenant travels as the correlation
            // id so a consumer can still order or shard by it; per-tenant
            // ordering is the outbox's job on this lane rather than the
            // broker's, which is a real difference from Service Bus sessions
            // and is stated here rather than glossed.
            CorrelationId = command.TenantId.ToString(),
            Type = command.Type,
            Persistent = true,
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queue,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command)),
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares the work queue bound to a dead-letter exchange, and the
    /// dead-letter queue behind it. Idempotent, and done by BOTH sides: a
    /// consumer that starts first must not fail because the producer has not
    /// run, and vice versa. Declaring with different arguments than an
    /// existing queue is an error in AMQP, which is why the arguments live in
    /// one method rather than being spelled out twice.
    /// </summary>
    internal static string DeadLetterExchangeFor(string queue) => $"{queue}.dlx";

    internal static async Task DeclareAsync(IChannel channel, string queue, CancellationToken ct)
    {
        var deadLetterExchange = DeadLetterExchangeFor(queue);
        var deadLetterQueue = $"{queue}.dead";

        await channel.ExchangeDeclareAsync(
            deadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: ct).ConfigureAwait(false);
        await channel.QueueDeclareAsync(
            deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct).ConfigureAwait(false);
        await channel.QueueBindAsync(
            deadLetterQueue, deadLetterExchange, routingKey: string.Empty,
            cancellationToken: ct).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = deadLetterExchange },
            cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>RabbitMQ consumer for the command queue.</summary>
public sealed class RabbitMqCommandTransport(
    IConfiguration configuration,
    ILogger<RabbitMqCommandTransport> logger) : ICommandTransport
{
    public async Task ConsumeAsync(
        Func<IntegrationCommand, CancellationToken, Task<CommandOutcome>> handle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var uri = configuration["Messaging:RabbitMqUri"];
        var queue = configuration["Messaging:CommandQueueName"];
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(queue))
        {
            RabbitMqLog.ConsumerNotConfigured(logger);
            return;
        }

        var factory = new ConnectionFactory { Uri = new Uri(uri) };
        await using var connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        await RabbitMqCommandSender.DeclareAsync(channel, queue, ct).ConfigureAwait(false);
        var deadLetterExchange = RabbitMqCommandSender.DeadLetterExchangeFor(queue);

        // Match the Service Bus processor's MaxConcurrentCalls: without a
        // prefetch bound one consumer takes the whole queue and the others sit
        // idle, which turns a scaled-out service into a single worker.
        await channel.BasicQosAsync(0, prefetchCount: 4, global: false, cancellationToken: ct)
            .ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            IntegrationCommand? command = null;
            try
            {
                command = JsonSerializer.Deserialize<IntegrationCommand>(
                    Encoding.UTF8.GetString(args.Body.Span));
            }
            catch (JsonException)
            {
                // Fall through to the unparseable path.
            }

            if (command is null)
            {
                await DeadLetterAsync(
                    channel, args, deadLetterExchange, "unparseable",
                    "Body is not an IntegrationCommand", ct).ConfigureAwait(false);
                return;
            }

            CommandOutcome outcome;
            try
            {
                outcome = await handle(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A handler that throws must not take the connection down with
                // it. Service Bus's processor swallows this into its error
                // callback; here it has to be explicit.
                RabbitMqLog.HandlerFailed(logger, ex);
                await DeadLetterAsync(
                    channel, args, deadLetterExchange, "handler_threw", ex.GetType().Name, ct)
                    .ConfigureAwait(false);
                return;
            }

            if (!outcome.Completed)
            {
                await DeadLetterAsync(
                    channel, args, deadLetterExchange, outcome.ReasonCode, outcome.ReasonMessage, ct)
                    .ConfigureAwait(false);
                return;
            }

            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: ct)
                .ConfigureAwait(false);
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken: ct)
            .ConfigureAwait(false);

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Dead-letter WITH the reason attached.
    ///
    /// A plain `BasicNack(requeue: false)` does route to the dead-letter
    /// exchange, and it is one line — but AMQP has nowhere to put a reason, so
    /// the dead-letter queue would fill with bodies and no account of why any
    /// of them were there. Service Bus takes a reason and a description
    /// natively; losing that on this lane would make the two transports
    /// meaningfully unequal in the one place an operator actually looks.
    ///
    /// So the message is republished to the DLX with the reason as headers,
    /// then the original is acked. That ordering is deliberate: publish first
    /// means a crash in between redelivers the original and we dead-letter
    /// twice, whereas acking first would LOSE it. A duplicate in a dead-letter
    /// queue is a nuisance; a silently dropped failed command is a bug someone
    /// finds months later.
    /// </summary>
    private static async Task DeadLetterAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        string deadLetterExchange,
        string code,
        string message,
        CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>(args.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            ["x-stratus-reason-code"] = Encoding.UTF8.GetBytes(code),
            ["x-stratus-reason"] = Encoding.UTF8.GetBytes(Truncate(message)),
        };

        var properties = new BasicProperties
        {
            MessageId = args.BasicProperties.MessageId,
            CorrelationId = args.BasicProperties.CorrelationId,
            Type = args.BasicProperties.Type,
            Persistent = true,
            Headers = headers,
        };

        await channel.BasicPublishAsync(
            exchange: deadLetterExchange,
            routingKey: string.Empty,
            mandatory: false,
            basicProperties: properties,
            body: args.Body.ToArray(),
            cancellationToken: ct).ConfigureAwait(false);

        await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// A handler's message is arbitrary text and headers are not the place for
    /// an unbounded one.
    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];
}

internal static partial class RabbitMqLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RabbitMQ is not configured; commands are not being sent.")]
    public static partial void SenderNotConfigured(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RabbitMQ is not configured; the command consumer is idle.")]
    public static partial void ConsumerNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command handler threw; dead-lettering.")]
    public static partial void HandlerFailed(ILogger logger, Exception exception);
}
