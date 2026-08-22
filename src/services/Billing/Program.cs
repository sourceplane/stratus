using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("billing");
builder.Services.AddOpenApi();
builder.Services.AddDbContext<StratusDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();
app.MapOpenApi();
app.MapDefaultEndpoints();

app.MapGet("/v1/tenants/{tenantId:guid}/subscription", async (Guid tenantId, StratusDbContext db, CancellationToken ct) =>
    await db.Subscriptions.SingleOrDefaultAsync(s => s.TenantId == tenantId, ct) is { } sub
        ? Results.Ok(new { sub.TenantId, sub.Plan, sub.UpdatedAt })
        : Results.Ok(new { TenantId = tenantId, Plan = "free", UpdatedAt = (DateTimeOffset?)null }));

app.MapPut("/v1/tenants/{tenantId:guid}/subscription", async (Guid tenantId, ChangePlan request, StratusDbContext db, CancellationToken ct) =>
{
    var sub = await db.Subscriptions.SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
    if (sub is null)
    {
        sub = new Subscription { TenantId = tenantId };
        db.Subscriptions.Add(sub);
    }

    sub.Plan = request.Plan;
    sub.UpdatedAt = DateTimeOffset.UtcNow;
    db.Enqueue(EventTypes.PlanChanged, tenantId, new { TenantId = tenantId, request.Plan });
    await db.SaveChangesAsync(ct);

    return Results.Ok(new { sub.TenantId, sub.Plan });
});

// The entitlement check the gateway calls on essentially every request. Kept
// deliberately trivial — this is the hot path the design earmarks for gRPC
// once there is traffic worth measuring.
app.MapGet("/v1/tenants/{tenantId:guid}/entitlements/{feature}", async (Guid tenantId, string feature, StratusDbContext db, CancellationToken ct) =>
{
    var plan = (await db.Subscriptions.SingleOrDefaultAsync(s => s.TenantId == tenantId, ct))?.Plan ?? "free";
    var allowed = plan != "free" || feature.StartsWith("core.", StringComparison.Ordinal);
    return Results.Ok(new { tenantId, feature, plan, allowed });
});

app.Run();

internal sealed record ChangePlan(string Plan);
