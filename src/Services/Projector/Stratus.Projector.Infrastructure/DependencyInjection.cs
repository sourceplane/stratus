using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stratus.Messaging;
using Stratus.Projector.Application;

namespace Stratus.Projector.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddProjectorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Which brokers this fleet talks to is a DEPLOYMENT fact, so it is read
        // from configuration here rather than compiled in.
        services.AddMessagingTransports(configuration);

        // Registering a new projection here is the ONLY change a new read model
        // needs — the dispatcher discovers it through IEnumerable<IProjection>.
        services.AddScoped<IProjection, TenantDirectoryProjection>();
        services.AddScoped<IProjectionDispatcher, ProjectionDispatcher>();
        services.AddHostedService<EventConsumer>();

        return services;
    }
}
