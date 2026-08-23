using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.Notifier.Application;

namespace Stratus.Notifier.Infrastructure;

/// <summary>
/// Drains the command queue into the notification handler.
///
/// Like the projector's consumer, this no longer names a broker: queue names,
/// credentials, concurrency and — the important one — how a message is settled
/// all live behind <see cref="ICommandTransport"/>.
///
/// The one thing this file still decides is the TRANSLATION: a failed
/// <c>Result</c> becomes a dead letter carrying the handler's own error code
/// and message. That is a policy choice, not a transport detail, and it is
/// deliberately visible here rather than buried in the broker code — "a
/// handler failure dead-letters" is the sort of decision that should be
/// readable in the service that owns the work.
/// </summary>
public sealed class CommandConsumer(
    ICommandTransport transport,
    IServiceScopeFactory scopes) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        transport.ConsumeAsync(HandleAsync, stoppingToken);

    private async Task<CommandOutcome> HandleAsync(IntegrationCommand command, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<INotificationHandler>();
        var result = await handler.HandleAsync(command, ct).ConfigureAwait(false);

        return result.IsSuccess
            ? CommandOutcome.Complete()
            : CommandOutcome.DeadLetter(result.Error.Code, result.Error.Message);
    }
}
