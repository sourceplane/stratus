using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratus.Billing.Application;
using Stratus.BuildingBlocks;
using Stratus.Messaging;

namespace Stratus.Billing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBillingInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<BillingDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddScoped<IOutboxDbContext>(sp => sp.GetRequiredService<BillingDbContext>());
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<IIntegrationEventQueue, OutboxIntegrationEventQueue>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    public static IServiceCollection AddBillingApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        return services;
    }
}
