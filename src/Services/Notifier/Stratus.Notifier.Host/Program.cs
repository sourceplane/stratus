using Stratus.Notifier.Infrastructure;
using Stratus.ServiceDefaults;

// A worker still runs as a web host: Container Apps and Coolify both probe
// HTTP for health, and a worker with no health endpoint is a worker nothing
// can tell is alive.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("notifier");
builder.Services.AddNotifierInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
