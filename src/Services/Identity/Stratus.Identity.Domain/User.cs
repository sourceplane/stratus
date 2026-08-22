using Stratus.BuildingBlocks;

namespace Stratus.Identity.Domain;

/// <summary>
/// A person who can sign in. The aggregate root for the identity context: it
/// owns its invariants, and nothing outside reaches past it to mutate state.
/// </summary>
public sealed class User : Entity, IAggregateRoot
{
    private User() { }

    private User(Guid id, string email, DateTimeOffset createdAt) : base(id)
    {
        Email = email;
        CreatedAt = createdAt;
    }

    public string Email { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsLocked { get; private set; }

    /// <summary>
    /// The only way to make a User. A constructor that accepted an invalid
    /// address would let an invalid User exist, however briefly — this returns
    /// the failure instead of throwing, because a malformed email is an
    /// expected outcome of a public API, not an exceptional one.
    /// </summary>
    public static Result<User> Register(string email, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(email))
        {
            return Error.Validation("Email is required.");
        }

        var trimmed = email.Trim();
        if (trimmed.Length > 320 || !trimmed.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("Email is not a valid address.");
        }

        return new User(Guid.CreateVersion7(), trimmed.ToLowerInvariant(), clock.UtcNow);
    }

    public Result<User> Lock()
    {
        if (IsLocked)
        {
            return Error.Conflict("User is already locked.");
        }

        IsLocked = true;
        return this;
    }
}
