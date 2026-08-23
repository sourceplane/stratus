using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stratus.Messaging;

/// <summary>
/// Transport selection — the one place the baseline decides which brokers it
/// is running against.
///
/// `Messaging:Provider` names the transport family, not a broker: "azure" is
/// Event Hubs + Service Bus. It defaults to azure so an existing deployment
/// that sets nothing keeps its behaviour exactly.
///
/// An UNRECOGNISED value throws at startup rather than falling back. A typo in
/// a config key that silently selected the default would produce a fleet
/// pointing at the wrong brokers, reporting healthy, and consuming nothing —
/// the failure mode this whole seam exists to avoid. Fail at boot, name the
/// value, list what is available.
/// </summary>
public static class MessagingRegistration
{
    public const string ProviderKey = "Messaging:Provider";

    public const string AzureProvider = "azure";

    public static IServiceCollection AddMessagingTransports(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = configuration[ProviderKey]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider))
        {
            provider = AzureProvider;
        }

        switch (provider)
        {
            case AzureProvider:
                services.AddSingleton<IEventTransport, EventHubsEventTransport>();
                services.AddSingleton<ICommandTransport, ServiceBusCommandTransport>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown {ProviderKey} '{provider}'. Supported: {AzureProvider}.");
        }

        return services;
    }
}
