using Microsoft.EntityFrameworkCore;
using Stratus.BuildingBlocks;

namespace Stratus.Messaging;

/// <summary>
/// A pending integration event, written in the same transaction as the state
/// change that produced it.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public string Type { get; init; } = string.Empty;

    public Guid TenantId { get; init; }

    public string Payload { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DispatchedAt { get; set; }
}

/// <summary>Consumer-side dedupe record. At-least-once delivery demands it.</summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; init; }

    public string Consumer { get; init; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The contract a service's DbContext honours to take part in the outbox.
/// Implemented by Infrastructure; the dispatcher depends on this and never on
/// any concrete context.
///
/// Deliberately narrow: it declares ONLY the outbox's own sets. Re-declaring
/// SaveChangesAsync or Database here would make every call on a
/// `where T : DbContext, IOutboxDbContext` type parameter ambiguous between
/// the two constraints (CS0229) — the dispatcher gets those members from
/// DbContext, and this interface adds only what DbContext lacks.
/// </summary>
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> Outbox { get; }

    DbSet<ProcessedMessage> ProcessedMessages { get; }
}

/// <summary>
/// Writes integration events into the outbox rather than to a broker, so the
/// event is committed atomically with the state change. Nothing in the
/// Application layer learns that a broker exists.
/// </summary>
public sealed class OutboxIntegrationEventQueue(IOutboxDbContext context, IClock clock) : IIntegrationEventQueue
{
    public void Enqueue(string type, Guid tenantId, object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        context.Outbox.Add(new OutboxMessage
        {
            Type = type,
            TenantId = tenantId,
            Payload = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = clock.UtcNow,
        });
    }
}

/// <summary>Model configuration shared by every service's DbContext.</summary>
public static class OutboxModel
{
    public static void ApplyOutbox(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();
            // The dispatcher only ever scans undispatched rows; a full index
            // would grow without bound serving a query nobody runs.
            //
            // The filter is raw SQL, so it depends on UseSnakeCaseColumns
            // having run: Postgres folds an unquoted identifier to lower case,
            // and EF's default column name is the quoted, case-SENSITIVE
            // "DispatchedAt". Without the convention this filter names a
            // column that does not exist and the migration fails to apply.
            e.HasIndex(x => x.CreatedAt).HasFilter("dispatched_at IS NULL");
        });

        builder.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_messages");
            e.HasKey(x => new { x.MessageId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(120).IsRequired();
        });
    }
}
