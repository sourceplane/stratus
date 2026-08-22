using Stratus.Messaging;
using Stratus.ServiceDefaults;
using Stratus.Tenancy.Infrastructure;
using Stratus.Tenancy.Web.Controllers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("tenancy");

builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(TenantsController).Assembly);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddTenancyApplication();
builder.Services.AddTenancyInfrastructure(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."));

builder.Services.AddHostedService<OutboxDispatcher<TenancyDbContext>>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;
