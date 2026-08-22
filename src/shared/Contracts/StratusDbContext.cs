using Microsoft.EntityFrameworkCore;

namespace Stratus.Contracts;

public class StratusDbContext(DbContextOptions<StratusDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<Membership>().HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        b.Entity<Subscription>().HasIndex(s => s.TenantId).IsUnique();

        // The dispatcher only ever scans undispatched rows; indexing the whole
        // table would grow without bound for a query that never reads it.
        b.Entity<OutboxMessage>()
            .HasIndex(o => o.CreatedAt)
            .HasFilter("\"DispatchedAt\" IS NULL");

        b.Entity<ProcessedMessage>().HasKey(p => new { p.MessageId, p.Consumer });

        base.OnModelCreating(b);
    }
}
