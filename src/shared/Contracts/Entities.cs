using System.ComponentModel.DataAnnotations;

namespace Stratus.Contracts;

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(80)] public string Slug { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    [MaxLength(320)] public string Email { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Membership
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(40)] public string Role { get; set; } = "member";
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    [MaxLength(40)] public string Plan { get; set; } = "free";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The transactional outbox. A service must not be able to write state without
/// publishing, or publish without writing — both happen in one transaction, and
/// a dispatcher drains this table afterwards.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    [MaxLength(200)] public string Type { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DispatchedAt { get; set; }
}

/// <summary>
/// Consumer-side dedupe. The outbox gives at-least-once delivery, so every
/// consumer records what it has already handled rather than hoping.
/// </summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; set; }
    [MaxLength(120)] public string Consumer { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
