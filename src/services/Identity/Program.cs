using Microsoft.EntityFrameworkCore;
using Stratus.Contracts;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("identity");
builder.Services.AddOpenApi();
builder.Services.AddDbContext<StratusDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();
app.MapOpenApi();
app.MapDefaultEndpoints();

app.MapPost("/v1/users", async (RegisterUser request, StratusDbContext db, CancellationToken ct) =>
{
    var user = new User { Email = request.Email };
    db.Users.Add(user);

    // State change and event in ONE transaction — the outbox is what makes
    // "saved but never announced" impossible.
    db.Enqueue(EventTypes.UserRegistered, request.TenantId, new { user.Id, user.Email });
    await db.SaveChangesAsync(ct);

    return Results.Created($"/v1/users/{user.Id}", new { user.Id, user.Email });
});

app.MapGet("/v1/users/{id:guid}", async (Guid id, StratusDbContext db, CancellationToken ct) =>
    await db.Users.FindAsync([id], ct) is { } user
        ? Results.Ok(new { user.Id, user.Email })
        : Results.NotFound());

app.Run();

internal sealed record RegisterUser(string Email, Guid TenantId);
