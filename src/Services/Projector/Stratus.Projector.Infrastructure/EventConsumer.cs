using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratus.Messaging;
using Stratus.Projector.Application;

namespace Stratus.Projector.Infrastructure;

/// <summary>
/// Drains the durable event log into the read models.
///
/// It no longer names a broker. Everything vendor-specific — which endpoint,
/// which credential, how an unparseable body is skipped — moved to
/// <see cref="IEventTransport"/> in Stratus.Messaging, which is already the
/// only project in the solution that references a messaging SDK. What is left
/// here is the part that is actually the projector's: open a scope per event
/// and dispatch it.
///
/// The scope matters. The dispatcher resolves a DbContext, so a single
/// long-lived scope across the whole consume loop would hold one connection
/// open for the life of the service and accumulate tracked entities from every
/// event it had ever seen.
/// </summary>
public sealed class EventConsumer(
    IEventTransport transport,
    IServiceScopeFactory scopes) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        transport.ConsumeAsync(
            async (@event, ct) =>
            {
                using var scope = scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IProjectionDispatcher>();
                await dispatcher.DispatchAsync(@event, ct).ConfigureAwait(false);
            },
            stoppingToken);
}
