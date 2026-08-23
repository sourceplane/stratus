using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stratus.Messaging;
using Stratus.Notifier.Application;

namespace Stratus.Notifier.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNotifierInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Which brokers this fleet talks to is a DEPLOYMENT fact, so it is read
        // from configuration here rather than compiled in.
        services.AddMessagingTransports(configuration);

        services.AddScoped<INotificationChannel, LoggingNotificationChannel>();
        services.AddScoped<INotificationHandler, NotificationHandler>();
        services.AddHostedService<CommandConsumer>();

        return services;
    }
}
