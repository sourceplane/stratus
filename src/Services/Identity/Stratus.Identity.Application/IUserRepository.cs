using Stratus.BuildingBlocks;
using Stratus.Identity.Domain;

namespace Stratus.Identity.Application;

/// <summary>
/// Declared here, implemented in Infrastructure. This is the Dependency
/// Inversion Principle made structural: the use case owns the contract, and the
/// database adapter conforms to it — not the other way round.
/// </summary>
public interface IUserRepository : IRepository<User>
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
}
