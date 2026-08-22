using Stratus.Billing.Infrastructure;
using Stratus.Billing.Web.Controllers;
using Stratus.Messaging;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("billing");

builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(SubscriptionsController).Assembly);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddBillingApplication();
builder.Services.AddBillingInfrastructure(
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."));

builder.Services.AddHostedService<OutboxDispatcher<BillingDbContext>>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;
