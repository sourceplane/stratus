namespace Stratus.Contracts;

/// <summary>
/// The envelope every domain event travels in. Versioned from the first commit:
/// adding a field to a payload is cheap, discovering you cannot tell two
/// generations of an event apart is not.
/// </summary>
/// <param name="EventId">Idempotency key. Consumers dedupe on this.</param>
/// <param name="Type">Routing key, <c>&lt;context&gt;.&lt;entity&gt;.&lt;verb&gt;</c>.</param>
/// <param name="TenantId">Partition key — per-tenant ordering, cross-tenant parallelism.</param>
public sealed record EventEnvelope(
    Guid EventId,
    string Type,
    Guid TenantId,
    DateTimeOffset OccurredAt,
    int Version,
    string Payload);

public static class EventTypes
{
    public const string UserRegistered = "identity.user.registered";
    public const string TenantCreated = "tenancy.tenant.created";
    public const string MemberInvited = "tenancy.member.invited";
    public const string PlanChanged = "billing.plan.changed";
}

/// <summary>A command addressed to exactly one owner, with an expectation of completion.</summary>
public sealed record CommandEnvelope(
    Guid CommandId,
    string Type,
    Guid TenantId,
    string Payload);

public static class CommandTypes
{
    public const string SendNotification = "notifier.notification.send";
}
