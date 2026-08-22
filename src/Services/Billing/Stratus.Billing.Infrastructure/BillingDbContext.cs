using Microsoft.EntityFrameworkCore;
using Stratus.Billing.Domain;
using Stratus.BuildingBlocks;
using Stratus.Messaging;

namespace Stratus.Billing.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : DbContext(options), IUnitOfWork, IOutboxDbContext
{
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.PlanCode).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.TenantId).IsUnique();
            // Plan is derived from PlanCode; persisting it too would create a
            // second source of truth that can disagree with the first.
            e.Ignore(x => x.Plan);
        });

        modelBuilder.ApplyOutbox();

        base.OnModelCreating(modelBuilder);
    }
}
