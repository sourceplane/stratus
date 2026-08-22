using Stratus.BuildingBlocks;

namespace Stratus.Billing.Domain;

/// <summary>
/// A plan, expressed as a type rather than a string. Entitlement questions are
/// answered here, so "is this tenant allowed to do X" has exactly one
/// implementation instead of one per calling service.
/// </summary>
public sealed record Plan(string Code, int IncludedProjects, bool AllowsSso)
{
    public static readonly Plan Free = new("free", 1, false);
    public static readonly Plan Team = new("team", 25, false);
    public static readonly Plan Enterprise = new("enterprise", int.MaxValue, true);

    public static readonly IReadOnlyList<Plan> All = [Free, Team, Enterprise];

    public static Result<Plan> FromCode(string code)
    {
        var match = All.FirstOrDefault(p =>
            string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? Error.Validation($"Unknown plan '{code}'. Valid plans: {string.Join(", ", All.Select(p => p.Code))}.")
            : match;
    }

    public bool Allows(string feature) => feature switch
    {
        "sso" => AllowsSso,
        _ => !string.Equals(Code, Free.Code, StringComparison.Ordinal)
             || feature.StartsWith("core.", StringComparison.Ordinal),
    };
}

public sealed class Subscription : Entity, IAggregateRoot
{
    private Subscription() { }

    private Subscription(Guid id, Guid tenantId, string planCode, DateTimeOffset updatedAt) : base(id)
    {
        TenantId = tenantId;
        PlanCode = planCode;
        UpdatedAt = updatedAt;
    }

    public Guid TenantId { get; private set; }

    public string PlanCode { get; private set; } = Plan.Free.Code;

    public DateTimeOffset UpdatedAt { get; private set; }

    public Plan Plan => Plan.All.First(p =>
        string.Equals(p.Code, PlanCode, StringComparison.OrdinalIgnoreCase));

    public static Subscription StartFree(Guid tenantId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new Subscription(Guid.CreateVersion7(), tenantId, Plan.Free.Code, clock.UtcNow);
    }

    public Result<Subscription> ChangeTo(string planCode, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var plan = Plan.FromCode(planCode);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        if (string.Equals(plan.Value.Code, PlanCode, StringComparison.Ordinal))
        {
            return Error.Conflict($"Subscription is already on the '{PlanCode}' plan.");
        }

        PlanCode = plan.Value.Code;
        UpdatedAt = clock.UtcNow;
        return this;
    }
}
