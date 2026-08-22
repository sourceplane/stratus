namespace Stratus.BuildingBlocks;

/// <summary>
/// Declared by the Application layer, implemented by Infrastructure. The
/// dependency points inward: business logic states what persistence it needs
/// and never learns what provides it.
/// </summary>
public interface IRepository<T> where T : Entity, IAggregateRoot
{
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);

    void Add(T entity);
}

/// <summary>
/// One transaction boundary per use case. Committing is the caller's decision
/// — a repository that saved on every Add would make an atomic multi-step use
/// case impossible to express, and the outbox depends on that atomicity.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Queues an integration event for publication inside the caller's
/// transaction. The implementation writes to the outbox; nothing in the
/// Application layer knows a broker exists.
/// </summary>
public interface IIntegrationEventQueue
{
    void Enqueue(string type, Guid tenantId, object payload);
}

/// <summary>Injected rather than read statically, so time is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
