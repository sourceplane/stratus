using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

namespace Stratus.ServiceDefaults;

/// <summary>
/// The service's own name, captured at <c>AddServiceDefaults</c> so the mapping
/// half can use it without every host repeating it. Passing it twice is how the
/// OpenAPI document name and the Scalar route drift apart.
/// </summary>
/// <param name="Name">The short service name, e.g. <c>identity</c>.</param>
internal sealed record ServiceIdentity(string Name);

/// <summary>
/// The cross-cutting concerns no service should restate: telemetry, health,
/// resilience, and the API reference. Wired once so the whole fleet reports
/// identically.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Set <c>OpenApi:Exposed</c> to <c>false</c> to withhold the document and
    /// the reference UI from a deployment.
    /// </summary>
    private const string ExposedKey = "OpenApi:Exposed";

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        builder.Services.AddSingleton(new ServiceIdentity(serviceName));

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        // Readiness may gate on dependencies; liveness never does — a
        // dependency outage must not become a fleet-wide restart storm.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        // The document is named after the SERVICE, not "v1". Six services each
        // publishing `/openapi/v1.json` cannot be told apart once they are
        // behind one gateway, and the gateway is the only public surface here.
        if (IsExposed(builder.Configuration))
        {
            builder.Services.AddOpenApi(serviceName);
        }

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready");
        app.MapHealthChecks("/health");

        if (IsExposed(app.Configuration))
        {
            var serviceName = app.Services.GetRequiredService<ServiceIdentity>().Name;

            // Service-scoped paths, deliberately. Every route below is unique
            // across the fleet, so the gateway can forward `/docs/identity` and
            // `/openapi/identity.json` to identity WITHOUT rewriting the path —
            // and a path-preserving proxy is the only kind whose relative asset
            // and document links still resolve on the far side.
            app.MapOpenApi("/openapi/{documentName}.json");
            app.MapScalarApiReference($"/docs/{serviceName}", options =>
            {
                options
                    .WithTitle($"{serviceName} — API reference")
                    .WithOpenApiRoutePattern($"/openapi/{serviceName}.json");
            });
        }

        return app;
    }

    private static bool IsExposed(IConfiguration configuration)
        // Default ON: this is a baseline, and an API you cannot see is one
        // nobody can verify. A product hardening its public edge sets the key.
        => configuration.GetValue(ExposedKey, defaultValue: true);
}
