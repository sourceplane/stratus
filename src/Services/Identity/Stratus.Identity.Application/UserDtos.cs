namespace Stratus.Identity.Application;

/// <summary>What the outside world sends in. Never a domain entity.</summary>
public sealed record RegisterUserCommand(string Email, Guid TenantId);

/// <summary>What the outside world gets back. Never a domain entity either —
/// the wire shape and the model must be free to change independently.</summary>
public sealed record UserDto(Guid Id, string Email, DateTimeOffset CreatedAt, bool IsLocked);
