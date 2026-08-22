using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("tenancy");
builder.Services.AddOpenApi();
builder.Services.AddDbContext<StratusDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();
app.MapOpenApi();
app.MapDefaultEndpoints();

app.MapPost("/v1/tenants", async (CreateTenant request, StratusDbContext db, CancellationToken ct) =>
{
    var tenant = new Tenant { Name = request.Name, Slug = request.Slug };
    db.Tenants.Add(tenant);
    db.Enqueue(EventTypes.TenantCreated, tenant.Id, new { tenant.Id, tenant.Name, tenant.Slug });
    await db.SaveChangesAsync(ct);

    return Results.Created($"/v1/tenants/{tenant.Id}", new { tenant.Id, tenant.Slug });
});

app.MapGet("/v1/tenants/{id:guid}", async (Guid id, StratusDbContext db, CancellationToken ct) =>
    await db.Tenants.FindAsync([id], ct) is { } tenant
        ? Results.Ok(new { tenant.Id, tenant.Name, tenant.Slug })
        : Results.NotFound());

app.MapGet("/v1/tenants/{id:guid}/members", async (Guid id, StratusDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Memberships.Where(m => m.TenantId == id)
        .Select(m => new { m.UserId, m.Role }).ToListAsync(ct)));

app.MapPost("/v1/tenants/{id:guid}/members", async (Guid id, AddMember request, StratusDbContext db, CancellationToken ct) =>
{
    db.Memberships.Add(new Membership { TenantId = id, UserId = request.UserId, Role = request.Role });
    db.Enqueue(EventTypes.MemberInvited, id, new { request.UserId, request.Role });
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.Run();

internal sealed record CreateTenant(string Name, string Slug);
internal sealed record AddMember(Guid UserId, string Role);
