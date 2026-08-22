using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stratus.BuildingBlocks.Web;
using Stratus.Identity.Application;

namespace Stratus.Identity.Web.Controllers;

/// <summary>
/// Thin by construction: it binds the request, calls one use case, and lets the
/// base class turn the Result into a status code. No business rule lives here,
/// and there is no branch on domain state to tempt one in.
/// </summary>
[Route("v1/users")]
public sealed class UsersController(IUserService users) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Register(
        [FromBody] RegisterUserCommand command,
        CancellationToken ct)
    {
        var result = await users.RegisterAsync(command, ct);
        return CreatedFromResult(result, u => $"/v1/users/{u.Id}", u => u);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> Get(Guid id, CancellationToken ct) =>
        FromResult(await users.GetAsync(id, ct), u => u);

    [HttpPost("{id:guid}/lock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Lock(Guid id, CancellationToken ct) =>
        FromResult(await users.LockAsync(id, ct), u => u);
}
