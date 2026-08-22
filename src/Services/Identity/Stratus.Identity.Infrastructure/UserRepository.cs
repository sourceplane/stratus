using Microsoft.EntityFrameworkCore;
using Stratus.Identity.Application;
using Stratus.Identity.Domain;

namespace Stratus.Identity.Infrastructure;

/// <summary>
/// The Application layer's contract, satisfied by EF Core. Queries live here so
/// a use case never holds an IQueryable — which is what stops persistence
/// concerns leaking upward one LINQ expression at a time.
/// </summary>
public sealed class UserRepository(IdentityDbContext context) : IUserRepository
{
    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public void Add(User entity) => context.Users.Add(entity);

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalised = email.Trim().ToLowerInvariant();
        return context.Users.AnyAsync(u => u.Email == normalised, ct);
    }
}
