using Microsoft.Extensions.DependencyInjection;
using Stratus.Projector.Application;

namespace Stratus.Projector.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProjectorInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registering a new projection here is the ONLY change a new read model
        // needs — the dispatcher discovers it through IEnumerable<IProjection>.
        services.AddScoped<IProjection, TenantDirectoryProjection>();
        services.AddScoped<IProjectionDispatcher, ProjectionDispatcher>();
        services.AddHostedService<EventConsumer>();

        return services;
    }
}
