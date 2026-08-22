using Microsoft.Extensions.DependencyInjection;
using Stratus.Notifier.Application;

namespace Stratus.Notifier.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNotifierInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<INotificationChannel, LoggingNotificationChannel>();
        services.AddScoped<INotificationHandler, NotificationHandler>();
        services.AddHostedService<CommandConsumer>();

        return services;
    }
}
