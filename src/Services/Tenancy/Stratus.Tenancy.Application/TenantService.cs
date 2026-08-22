using Stratus.BuildingBlocks;
using Stratus.Contracts;
using Stratus.Tenancy.Domain;

namespace Stratus.Tenancy.Application;

public sealed record CreateTenantCommand(string Name, string Slug);

public sealed record AddMemberCommand(Guid UserId, string Role);

public sealed record TenantDto(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt);

public sealed record MemberDto(Guid UserId, string Role);

public interface ITenantRepository : IRepository<Tenant>
{
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    Task<Tenant?> GetWithMembersAsync(Guid id, CancellationToken ct = default);
}

public interface ITenantService
{
    Task<Result<TenantDto>> CreateAsync(CreateTenantCommand command, CancellationToken ct = default);

    Task<Result<TenantDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<IReadOnlyCollection<MemberDto>>> GetMembersAsync(Guid id, CancellationToken ct = default);

    Task<Result<MemberDto>> AddMemberAsync(Guid tenantId, AddMemberCommand command, CancellationToken ct = default);
}

public sealed class TenantService(
    ITenantRepository tenants,
    IUnitOfWork unitOfWork,
    IIntegrationEventQueue events,
    IClock clock) : ITenantService
{
    public async Task<Result<TenantDto>> CreateAsync(CreateTenantCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await tenants.SlugExistsAsync(command.Slug, ct).ConfigureAwait(false))
        {
            return Error.Conflict($"Slug '{command.Slug}' is already taken.");
        }

        var created = Tenant.Create(command.Name, command.Slug, clock);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var tenant = created.Value;
        tenants.Add(tenant);
        events.Enqueue(EventTypes.TenantCreated, tenant.Id, new { tenant.Id, tenant.Name, tenant.Slug });

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(tenant);
    }

    public async Task<Result<TenantDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await tenants.GetAsync(id, ct).ConfigureAwait(false);
        return tenant is null ? Error.NotFound($"Tenant {id} was not found.") : ToDto(tenant);
    }

    public async Task<Result<IReadOnlyCollection<MemberDto>>> GetMembersAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await tenants.GetWithMembersAsync(id, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound($"Tenant {id} was not found.");
        }

        IReadOnlyCollection<MemberDto> members =
            [.. tenant.Members.Select(m => new MemberDto(m.UserId, m.Role))];
        return Result<IReadOnlyCollection<MemberDto>>.Success(members);
    }

    public async Task<Result<MemberDto>> AddMemberAsync(
        Guid tenantId,
        AddMemberCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tenant = await tenants.GetWithMembersAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound($"Tenant {tenantId} was not found.");
        }

        // The aggregate enforces the rules; the service only orchestrates.
        var added = tenant.AddMember(command.UserId, command.Role);
        if (!added.IsSuccess)
        {
            return added.Error;
        }

        events.Enqueue(EventTypes.MemberAdded, tenantId, new { command.UserId, command.Role });
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return new MemberDto(added.Value.UserId, added.Value.Role);
    }

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.Name, t.Slug, t.CreatedAt);
}
