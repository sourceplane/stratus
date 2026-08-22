namespace Stratus.BuildingBlocks;

/// <summary>
/// Identity-based equality. Two entities are the same entity when their ids
/// match, regardless of what their other fields say — which is the whole point
/// of having an identity rather than being a value.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id) => Id = id;

    // EF Core materialises through this; it is not part of the public surface.
    protected Entity() { }

    public Guid Id { get; protected init; } = Guid.CreateVersion7();

    public bool Equals(Entity? other) =>
        other is not null && GetType() == other.GetType() && Id == other.Id;

    public override bool Equals(object? obj) => Equals(obj as Entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>
/// Marks the one entity in a cluster through which the cluster is loaded and
/// saved. Repositories are defined per aggregate root and never per entity —
/// that constraint is what stops a repository layer from degenerating into a
/// second, worse ORM.
/// </summary>
public interface IAggregateRoot;
