using Microsoft.EntityFrameworkCore;
using Stratus.Billing.Application;
using Stratus.Billing.Domain;

namespace Stratus.Billing.Infrastructure;

public sealed class SubscriptionRepository(BillingDbContext context) : ISubscriptionRepository
{
    public Task<Subscription?> GetAsync(Guid id, CancellationToken ct = default) =>
        context.Subscriptions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Subscription?> GetByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        context.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

    public void Add(Subscription entity) => context.Subscriptions.Add(entity);
}
