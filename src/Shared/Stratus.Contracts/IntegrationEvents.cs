namespace Stratus.Contracts;

/// <summary>
/// The envelope every integration event travels in. Versioned from the first
/// commit: adding a payload field is cheap, discovering you cannot tell two
/// generations of an event apart is not.
/// </summary>
/// <param name="EventId">Idempotency key — consumers dedupe on this.</param>
/// <param name="Type">Routing key, <c>&lt;context&gt;.&lt;entity&gt;.&lt;verb&gt;</c>.</param>
/// <param name="TenantId">Partition key: per-tenant ordering, cross-tenant parallelism.</param>
public sealed record IntegrationEvent(
    Guid EventId,
    string Type,
    Guid TenantId,
    DateTimeOffset OccurredAt,
    int Version,
    string Payload);

/// <summary>A command with exactly one owner and an expectation of completion.</summary>
public sealed record IntegrationCommand(
    Guid CommandId,
    string Type,
    Guid TenantId,
    string Payload);

/// <summary>
/// This repo is the only place these strings are defined. A consumer that
/// spells one itself is a consumer that will silently stop receiving.
/// </summary>
public static class EventTypes
{
    public const string UserRegistered = "identity.user.registered";
    public const string TenantCreated = "tenancy.tenant.created";
    public const string MemberAdded = "tenancy.member.added";
    public const string PlanChanged = "billing.plan.changed";
}

public static class CommandTypes
{
    public const string SendNotification = "notifier.notification.send";
}
