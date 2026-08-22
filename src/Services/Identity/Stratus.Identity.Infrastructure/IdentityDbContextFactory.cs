using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stratus.Identity.Infrastructure;

/// <summary>
/// The design-time seam for `dotnet ef`. Without it the tools fall back to
/// booting the Host and resolving IdentityDbContext from its container, which fails
/// the moment ConnectionStrings:Postgres is unset — exactly the state a PR
/// lane and a migration-bundle build are in.
///
/// The connection string here is a placeholder and is never opened. Building a
/// migration or a bundle needs the MODEL, not a database; the real connection
/// arrives at apply time from the environment. Committing a reachable one would
/// also be the committed-credential defect verify-structure exists to catch.
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=stratus_identity_design;Username=postgres")
            .Options;

        return new IdentityDbContext(options);
    }
}
