using Microsoft.EntityFrameworkCore;
using Stratus.BuildingBlocks;
using Stratus.Identity.Domain;
using Stratus.Messaging;

namespace Stratus.Identity.Infrastructure;

/// <summary>
/// One context per bounded context. It implements <see cref="IUnitOfWork"/> so
/// the Application layer can commit a use case without ever naming EF Core, and
/// <see cref="IOutboxDbContext"/> so the shared dispatcher can drain it without
/// naming this context.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : DbContext(options), IUnitOfWork, IOutboxDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Configuration lives beside the context, never as attributes on the
        // domain entity — an entity annotated for a database is an entity that
        // has learned about persistence.
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.IsLocked).IsRequired();
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
