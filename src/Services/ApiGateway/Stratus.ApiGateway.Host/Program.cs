using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Stratus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("api-gateway");

// Routing is configuration, so adding a service behind the gateway is a config
// change rather than a gateway rebuild.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Per-tenant partitioning, so one noisy tenant cannot spend
            // another's budget. Falls back to the connection for unattributed
            // traffic.
            context.Request.Headers["X-Tenant-Id"].ToString() is { Length: > 0 } tenant
                ? tenant
                : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapReverseProxy();

app.Run();
