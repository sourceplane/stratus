using Microsoft.AspNetCore.Mvc;
using Stratus.BuildingBlocks.Web;
using Stratus.Tenancy.Application;

namespace Stratus.Tenancy.Web.Controllers;

[Route("v1/tenants")]
public sealed class TenantsController(ITenantService tenants) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantDto>> Create([FromBody] CreateTenantCommand command, CancellationToken ct)
    {
        var result = await tenants.CreateAsync(command, ct);
        return CreatedFromResult(result, t => $"/v1/tenants/{t.Id}", t => t);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDto>> Get(Guid id, CancellationToken ct) =>
        FromResult(await tenants.GetAsync(id, ct), t => t);

    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyCollection<MemberDto>>> GetMembers(Guid id, CancellationToken ct) =>
        FromResult(await tenants.GetMembersAsync(id, ct), m => m);

    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberDto>> AddMember(
        Guid id,
        [FromBody] AddMemberCommand command,
        CancellationToken ct)
    {
        var result = await tenants.AddMemberAsync(id, command, ct);
        return CreatedFromResult(result, _ => $"/v1/tenants/{id}/members", m => m);
    }
}
