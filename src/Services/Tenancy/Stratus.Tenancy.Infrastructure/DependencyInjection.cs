using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratus.BuildingBlocks;
using Stratus.Messaging;
using Stratus.Tenancy.Application;

namespace Stratus.Tenancy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTenancyInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<TenancyDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TenancyDbContext>());
        services.AddScoped<IOutboxDbContext>(sp => sp.GetRequiredService<TenancyDbContext>());
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IIntegrationEventQueue, OutboxIntegrationEventQueue>();
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    public static IServiceCollection AddTenancyApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ITenantService, TenantService>();
        return services;
    }
}
