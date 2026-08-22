using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>
/// The outbox delivers at-least-once, so every consumer dedupes rather than
/// hoping. This is a contract of the baseline, not a per-service choice — the
/// helper exists so the correct thing is also the easy thing.
/// </summary>
public static class Idempotency
{
    /// <summary>
    /// Runs <paramref name="handler"/> exactly once for a given message and
    /// consumer. Returns false when the message was already handled.
    /// </summary>
    public static async Task<bool> OnceAsync(
        StratusDbContext db,
        Guid messageId,
        string consumer,
        Func<Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(handler);

        var seen = await db.ProcessedMessages
            .AnyAsync(p => p.MessageId == messageId && p.Consumer == consumer, ct)
            .ConfigureAwait(false);

        if (seen)
        {
            return false;
        }

        await handler().ConfigureAwait(false);

        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = consumer });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two replicas raced on the same message. The composite primary key
            // is what actually enforces once-only; losing the race is a normal
            // outcome, not an error.
            return false;
        }

        return true;
    }
}
