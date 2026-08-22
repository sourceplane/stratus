using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>
/// Drains the outbox. FOR UPDATE SKIP LOCKED so several replicas can drain
/// concurrently without handing the same row to two of them; Postgres logical
/// replication is the documented upgrade when polling latency stops being
/// acceptable.
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopes,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxDbContext
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient publish failure must not kill the dispatcher: the
                // rows stay undispatched and the next tick retries them.
                logger.LogError(ex, "Outbox drain failed; retrying on the next tick.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await using var tx = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var batch = await context.Outbox
            .FromSqlRaw("""
                SELECT * FROM outbox_messages
                WHERE dispatched_at IS NULL
                ORDER BY created_at
                LIMIT 50
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return;
        }

        foreach (var row in batch)
        {
            await publisher.PublishAsync(
                new IntegrationEvent(row.Id, row.Type, row.TenantId, row.CreatedAt, 1, row.Payload),
                ct).ConfigureAwait(false);

            row.DispatchedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Dispatched {Count} outbox message(s).", batch.Count);
    }
}

/// <summary>
/// Runs a handler exactly once per (message, consumer). The outbox delivers
/// at-least-once, so this is a contract of the platform rather than a choice
/// each consumer makes independently.
/// </summary>
public static class Idempotency
{
    public static async Task<bool> OnceAsync(
        IOutboxDbContext context,
        Guid messageId,
        string consumer,
        Func<Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);

        if (await context.ProcessedMessages
                .AnyAsync(p => p.MessageId == messageId && p.Consumer == consumer, ct)
                .ConfigureAwait(false))
        {
            return false;
        }

        await handler().ConfigureAwait(false);

        context.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumer });

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two replicas raced. The composite key is what actually enforces
            // once-only; losing the race is a normal outcome, not an error.
            return false;
        }

        return true;
    }
}
