using Stratus.Contracts;

namespace Stratus.Messaging;

// The consume side of the seam, and the reason it exists.
//
// Publishing was already abstracted: a service depends on IEventPublisher or
// ICommandSender and never names a broker. Consuming was not — the projector's
// EventConsumer constructed an EventHubConsumerClient itself and the notifier's
// CommandConsumer constructed a ServiceBusClient, both in their Infrastructure
// layer. So half the messaging plane was portable and the other half named a
// vendor in two service projects, which is the asymmetry these interfaces
// close.
//
// That mattered on its own — an Infrastructure layer naming a cloud is exactly
// what the architecture suite exists to catch, and it slipped through because
// the rule only forbade it in Domain. It matters more now that the same
// baseline has to run on a self-hosted target, where the brokers are Kafka and
// RabbitMQ containers rather than Azure namespaces.
//
// ── Why callbacks rather than IAsyncEnumerable ──
//
// An `IAsyncEnumerable<IntegrationCommand>` reads better and is wrong: it
// throws away SETTLEMENT. A command is completed or dead-lettered, and only
// the transport knows how — Service Bus has a native dead-letter queue,
// Kafka has none and needs a produce to a DLQ topic, and neither decision
// belongs in a service. Handing the transport a delegate that returns an
// outcome keeps that where the vendor knowledge already is.

/// <summary>
/// What a handler decided, expressed in transport terms rather than domain
/// ones. Deliberately not <c>Result&lt;T&gt;</c>: the transport does not care
/// what a handler produced, only whether the message is finished with and, if
/// not, what to record on the dead letter.
/// </summary>
public readonly record struct CommandOutcome(bool Completed, string ReasonCode, string ReasonMessage)
{
    public static CommandOutcome Complete() => new(true, string.Empty, string.Empty);

    public static CommandOutcome DeadLetter(string code, string message) => new(false, code, message);
}

/// <summary>
/// Consumes the durable event log, dispatching each event to <paramref
/// name="handle"/>. Returns when the token trips.
///
/// An unconfigured broker must leave the service IDLE rather than throw: an
/// unconfigured deployment is a state to report, and a crash loop hides it
/// behind restart noise.
/// </summary>
public interface IEventTransport
{
    Task ConsumeAsync(Func<IntegrationEvent, CancellationToken, Task> handle, CancellationToken ct);
}

/// <summary>
/// Consumes the command queue. The delegate's <see cref="CommandOutcome"/>
/// decides settlement, so a handler failure dead-letters with its own reason
/// instead of being retried forever or silently dropped.
/// </summary>
public interface ICommandTransport
{
    Task ConsumeAsync(
        Func<IntegrationCommand, CancellationToken, Task<CommandOutcome>> handle,
        CancellationToken ct);
}
