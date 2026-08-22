using Microsoft.EntityFrameworkCore;
using Stratus.Tenancy.Application;
using Stratus.Tenancy.Domain;

namespace Stratus.Tenancy.Infrastructure;

public sealed class TenantRepository(TenancyDbContext context) : ITenantRepository
{
    public Task<Tenant?> GetAsync(Guid id, CancellationToken ct = default) =>
        context.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// Owned collections are not loaded by default. A caller that needs the
    /// members asks for this overload rather than discovering an empty list.
    /// </summary>
    public Task<Tenant?> GetWithMembersAsync(Guid id, CancellationToken ct = default) =>
        context.Tenants.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id, ct);

    public void Add(Tenant entity) => context.Tenants.Add(entity);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var normalised = slug.Trim();
        return context.Tenants.AnyAsync(t => t.Slug == normalised, ct);
    }
}
