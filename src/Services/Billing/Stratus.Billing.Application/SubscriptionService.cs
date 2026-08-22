using Stratus.Billing.Domain;
using Stratus.BuildingBlocks;
using Stratus.Contracts;

namespace Stratus.Billing.Application;

public sealed record ChangePlanCommand(string Plan);

public sealed record SubscriptionDto(Guid TenantId, string Plan, DateTimeOffset UpdatedAt);

public sealed record EntitlementDto(Guid TenantId, string Feature, string Plan, bool Allowed);

public interface ISubscriptionRepository : IRepository<Subscription>
{
    Task<Subscription?> GetByTenantAsync(Guid tenantId, CancellationToken ct = default);
}

public interface ISubscriptionService
{
    Task<Result<SubscriptionDto>> GetAsync(Guid tenantId, CancellationToken ct = default);

    Task<Result<SubscriptionDto>> ChangePlanAsync(Guid tenantId, ChangePlanCommand command, CancellationToken ct = default);

    Task<Result<EntitlementDto>> CheckAsync(Guid tenantId, string feature, CancellationToken ct = default);
}

public sealed class SubscriptionService(
    ISubscriptionRepository subscriptions,
    IUnitOfWork unitOfWork,
    IIntegrationEventQueue events,
    IClock clock) : ISubscriptionService
{
    public async Task<Result<SubscriptionDto>> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var subscription = await subscriptions.GetByTenantAsync(tenantId, ct).ConfigureAwait(false);

        // An unbilled tenant is on Free by definition, not an error — the
        // absence of a row is a valid state, so it is not reported as 404.
        return subscription is null
            ? new SubscriptionDto(tenantId, Plan.Free.Code, clock.UtcNow)
            : ToDto(subscription);
    }

    public async Task<Result<SubscriptionDto>> ChangePlanAsync(
        Guid tenantId,
        ChangePlanCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var subscription = await subscriptions.GetByTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (subscription is null)
        {
            subscription = Subscription.StartFree(tenantId, clock);
            subscriptions.Add(subscription);
        }

        var changed = subscription.ChangeTo(command.Plan, clock);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        events.Enqueue(EventTypes.PlanChanged, tenantId, new { TenantId = tenantId, Plan = subscription.PlanCode });
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return ToDto(subscription);
    }

    public async Task<Result<EntitlementDto>> CheckAsync(Guid tenantId, string feature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feature))
        {
            return Error.Validation("Feature is required.");
        }

        var subscription = await subscriptions.GetByTenantAsync(tenantId, ct).ConfigureAwait(false);
        var plan = subscription?.Plan ?? Plan.Free;

        return new EntitlementDto(tenantId, feature, plan.Code, plan.Allows(feature));
    }

    private static SubscriptionDto ToDto(Subscription s) => new(s.TenantId, s.PlanCode, s.UpdatedAt);
}
