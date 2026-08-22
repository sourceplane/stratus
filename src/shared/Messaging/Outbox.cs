using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>
/// Writes an event into the outbox in the SAME transaction as the state change.
/// A service must not be able to do one without the other.
/// </summary>
public static class OutboxExtensions
{
    public static void Enqueue(this StratusDbContext db, string type, Guid tenantId, object payload)
    {
        ArgumentNullException.ThrowIfNull(db);

        db.Outbox.Add(new OutboxMessage
        {
            Type = type,
            TenantId = tenantId,
            Payload = JsonSerializer.Serialize(payload),
        });
    }
}

/// <summary>
/// Drains the outbox. Polling with FOR UPDATE SKIP LOCKED so several replicas
/// can drain concurrently without handing the same row to two of them; logical
/// replication (CDC) is the documented upgrade when polling latency stops being
/// acceptable.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    ILogger<OutboxDispatcher> logger) : BackgroundService
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
                // A transient publish failure must not kill the dispatcher —
                // the rows stay undispatched and the next tick retries them.
                logger.LogError(ex, "Outbox drain failed; retrying next tick.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StratusDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var batch = await db.Outbox
            .FromSqlRaw("""
                SELECT * FROM "Outbox"
                WHERE "DispatchedAt" IS NULL
                ORDER BY "CreatedAt"
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
                new EventEnvelope(row.Id, row.Type, row.TenantId, row.CreatedAt, 1, row.Payload),
                ct).ConfigureAwait(false);
            row.DispatchedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Dispatched {Count} outbox message(s).", batch.Count);
    }
}
