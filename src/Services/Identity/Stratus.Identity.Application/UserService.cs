using Stratus.BuildingBlocks;
using Stratus.Contracts;
using Stratus.Identity.Domain;

namespace Stratus.Identity.Application;

/// <summary>
/// One use case per method, and each one is the whole transaction. The service
/// orchestrates; the entity decides. Everything it touches arrives as an
/// interface, so the whole class is testable without a database or a broker.
/// </summary>
public interface IUserService
{
    Task<Result<UserDto>> RegisterAsync(RegisterUserCommand command, CancellationToken ct = default);

    Task<Result<UserDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<Result<UserDto>> LockAsync(Guid id, CancellationToken ct = default);
}

public sealed class UserService(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IIntegrationEventQueue events,
    IClock clock) : IUserService
{
    public async Task<Result<UserDto>> RegisterAsync(RegisterUserCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await users.EmailExistsAsync(command.Email, ct).ConfigureAwait(false))
        {
            return Error.Conflict("A user with that email already exists.");
        }

        var created = User.Register(command.Email, clock);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        var user = created.Value;
        users.Add(user);

        // Queued INSIDE the same transaction: the row and the announcement
        // commit together or not at all.
        events.Enqueue(EventTypes.UserRegistered, command.TenantId, new { user.Id, user.Email });

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(user);
    }

    public async Task<Result<UserDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.GetAsync(id, ct).ConfigureAwait(false);
        return user is null ? Error.NotFound($"User {id} was not found.") : ToDto(user);
    }

    public async Task<Result<UserDto>> LockAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.GetAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound($"User {id} was not found.");
        }

        var locked = user.Lock();
        if (!locked.IsSuccess)
        {
            return locked.Error;
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(user);
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.CreatedAt, user.IsLocked);
}
