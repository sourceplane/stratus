using Stratus.Projector.Infrastructure;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("projector");
builder.Services.AddProjectorInfrastructure();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();
