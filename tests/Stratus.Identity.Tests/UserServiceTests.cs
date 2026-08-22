using Stratus.BuildingBlocks;
using Stratus.Identity.Application;
using Stratus.Identity.Domain;
using Xunit;

namespace Stratus.Identity.Tests;

/// <summary>
/// No database, no broker, no host. Every dependency is an interface declared
/// by the Application layer, so the use cases are testable in milliseconds —
/// which is the practical payoff of Dependency Inversion, not just the
/// theoretical one.
/// </summary>
public class UserServiceTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public async Task Register_persists_the_user_and_queues_the_event_together()
    {
        var repo = new FakeUserRepository();
        var uow = new FakeUnitOfWork();
        var events = new FakeEventQueue();
        var service = new UserService(repo, uow, events, new FixedClock());

        var result = await service.RegisterAsync(new RegisterUserCommand("Ada@Example.com ", Tenant));

        Assert.True(result.IsSuccess);
        Assert.Equal("ada@example.com", result.Value.Email);
        Assert.Single(repo.Added);
        Assert.Single(events.Queued);
        // One commit: the row and the announcement land in the same transaction.
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_email_without_saving()
    {
        var repo = new FakeUserRepository { EmailTaken = true };
        var uow = new FakeUnitOfWork();
        var events = new FakeEventQueue();
        var service = new UserService(repo, uow, events, new FixedClock());

        var result = await service.RegisterAsync(new RegisterUserCommand("ada@example.com", Tenant));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal(0, uow.SaveCount);
        Assert.Empty(events.Queued);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task Register_rejects_a_malformed_address(string email)
    {
        var service = new UserService(
            new FakeUserRepository(), new FakeUnitOfWork(), new FakeEventQueue(), new FixedClock());

        var result = await service.RegisterAsync(new RegisterUserCommand(email, Tenant));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Get_reports_a_missing_user_as_NotFound_rather_than_throwing()
    {
        var service = new UserService(
            new FakeUserRepository(), new FakeUnitOfWork(), new FakeEventQueue(), new FixedClock());

        var result = await service.GetAsync(Guid.CreateVersion7());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task Locking_an_already_locked_user_is_a_conflict()
    {
        var clock = new FixedClock();
        var user = User.Register("ada@example.com", clock).Value;
        user.Lock();

        var repo = new FakeUserRepository { Existing = user };
        var service = new UserService(repo, new FakeUnitOfWork(), new FakeEventQueue(), clock);

        var result = await service.LockAsync(user.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    private sealed class FakeUserRepository : IUserRepository
    {
        public List<User> Added { get; } = [];

        public bool EmailTaken { get; init; }

        public User? Existing { get; init; }

        public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Existing?.Id == id ? Existing : null);

        public void Add(User entity) => Added.Add(entity);

        public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(EmailTaken);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class FakeEventQueue : IIntegrationEventQueue
    {
        public List<string> Queued { get; } = [];

        public void Enqueue(string type, Guid tenantId, object payload) => Queued.Add(type);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
