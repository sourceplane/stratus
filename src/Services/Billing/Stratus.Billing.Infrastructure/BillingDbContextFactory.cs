using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stratus.Billing.Infrastructure;

/// <summary>
/// The design-time seam for `dotnet ef`. Without it the tools fall back to
/// booting the Host and resolving BillingDbContext from its container, which fails
/// the moment ConnectionStrings:Postgres is unset — exactly the state a PR
/// lane and a migration-bundle build are in.
///
/// The connection string here is a placeholder and is never opened. Building a
/// migration or a bundle needs the MODEL, not a database; the real connection
/// arrives at apply time from the environment. Committing a reachable one would
/// also be the committed-credential defect verify-structure exists to catch.
/// </summary>
public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=stratus_billing_design;Username=postgres")
            .Options;

        return new BillingDbContext(options);
    }
}
