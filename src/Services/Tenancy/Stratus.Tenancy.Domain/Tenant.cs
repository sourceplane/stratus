using Stratus.BuildingBlocks;

namespace Stratus.Tenancy.Domain;

/// <summary>
/// The aggregate root for a tenant and its members. Membership is reached only
/// through here — that is what makes "a member always belongs to a tenant" an
/// invariant the type system helps keep rather than a rule in a code review.
/// </summary>
public sealed class Tenant : Entity, IAggregateRoot
{
    private readonly List<Membership> _members = [];

    private Tenant() { }

    private Tenant(Guid id, string name, string slug, DateTimeOffset createdAt) : base(id)
    {
        Name = name;
        Slug = slug;
        CreatedAt = createdAt;
    }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<Membership> Members => _members.AsReadOnly();

    public static Result<Tenant> Create(string name, string slug, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("Tenant name is required.");
        }

        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 80)
        {
            return Error.Validation("Tenant slug is required and must be 80 characters or fewer.");
        }

        // Azure and Coolify resource names are stricter than a generic slug, so
        // the constraint lives here rather than being discovered at deploy.
        if (!slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return Error.Validation("Tenant slug may contain only lowercase letters, digits and hyphens.");
        }

        return new Tenant(Guid.CreateVersion7(), name.Trim(), slug.Trim(), clock.UtcNow);
    }

    public Result<Membership> AddMember(Guid userId, string role)
    {
        if (_members.Any(m => m.UserId == userId))
        {
            return Error.Conflict("That user is already a member of this tenant.");
        }

        if (!Membership.AllowedRoles.Contains(role))
        {
            return Error.Validation($"Role '{role}' is not one of: {string.Join(", ", Membership.AllowedRoles)}.");
        }

        var membership = Membership.For(Id, userId, role);
        _members.Add(membership);
        return membership;
    }
}

/// <summary>Part of the Tenant aggregate; never loaded or saved on its own.</summary>
public sealed class Membership : Entity
{
    public static readonly string[] AllowedRoles = ["owner", "admin", "member", "viewer"];

    private Membership() { }

    private Membership(Guid id, Guid tenantId, Guid userId, string role) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Role { get; private set; } = string.Empty;

    internal static Membership For(Guid tenantId, Guid userId, string role) =>
        new(Guid.CreateVersion7(), tenantId, userId, role);
}
