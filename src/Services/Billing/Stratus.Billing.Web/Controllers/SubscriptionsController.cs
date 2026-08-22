using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stratus.Billing.Application;
using Stratus.BuildingBlocks.Web;

namespace Stratus.Billing.Web.Controllers;

[Route("v1/billing/tenants/{tenantId:guid}")]
public sealed class SubscriptionsController(ISubscriptionService subscriptions) : ApiControllerBase
{
    [HttpGet("subscription")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubscriptionDto>> Get(Guid tenantId, CancellationToken ct) =>
        FromResult(await subscriptions.GetAsync(tenantId, ct), s => s);

    [HttpPut("subscription")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SubscriptionDto>> ChangePlan(
        Guid tenantId,
        [FromBody] ChangePlanCommand command,
        CancellationToken ct) =>
        FromResult(await subscriptions.ChangePlanAsync(tenantId, command, ct), s => s);

    /// <summary>
    /// The hot path: called on essentially every request through the gateway.
    /// This is the endpoint the design earmarks for gRPC once there is traffic
    /// worth measuring.
    /// </summary>
    [HttpGet("entitlements/{feature}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EntitlementDto>> Check(Guid tenantId, string feature, CancellationToken ct) =>
        FromResult(await subscriptions.CheckAsync(tenantId, feature, ct), e => e);
}
