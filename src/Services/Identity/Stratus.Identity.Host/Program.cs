using Stratus.Identity.Infrastructure;
using Stratus.Identity.Web.Controllers;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("identity");

// Controllers live in a separate assembly, so the host has to say where to
// look for them. This is the seam that keeps the web layer swappable.
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(UsersController).Assembly);

builder.Services.AddProblemDetails();

builder.Services.AddIdentityApplication();
builder.Services.AddIdentityInfrastructure(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."));

builder.Services.AddHostedService<OutboxDispatcher<IdentityDbContext>>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

/// <summary>Exposed so the integration tests can drive the real host.</summary>
public partial class Program;
