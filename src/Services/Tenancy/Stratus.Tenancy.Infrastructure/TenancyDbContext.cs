using Microsoft.EntityFrameworkCore;
using Stratus.BuildingBlocks;
using Stratus.Messaging;
using Stratus.Tenancy.Domain;

namespace Stratus.Tenancy.Infrastructure;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options)
    : DbContext(options), IUnitOfWork, IOutboxDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();

            // Members are owned by the aggregate root: EF loads and saves them
            // with the Tenant and gives them no DbSet of their own, which is
            // the persistence mirror of the domain rule.
            e.OwnsMany(x => x.Members, m =>
            {
                m.ToTable("memberships");
                m.WithOwner().HasForeignKey(x => x.TenantId);
                m.HasKey(x => x.Id);
                m.Property(x => x.Role).HasMaxLength(40).IsRequired();
                m.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            });
        });

        modelBuilder.ApplyOutbox();

        // LAST, so it sees every entity above. Postgres folds unquoted
        // identifiers to lower case while EF quotes PascalCase ones, so raw
        // SQL and the default mapping silently disagree; this settles it in
        // one place for the whole context.
        modelBuilder.UseSnakeCaseColumns();

        base.OnModelCreating(modelBuilder);
    }
}
