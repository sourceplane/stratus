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

    /// <summary>
    /// Self-hosted Kafka + RabbitMQ — the pair a Coolify or plain-Docker
    /// target runs. Named for the family rather than the brokers so a swap
    /// inside it (Redpanda for Kafka, say) is not a config-breaking change for
    /// every deployment.
    /// </summary>
    public const string OssProvider = "oss";

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

            case OssProvider:
                services.AddSingleton<IEventTransport, KafkaEventTransport>();
                services.AddSingleton<ICommandTransport, RabbitMqCommandTransport>();
                // The PUBLISH side too. The Azure lane registers its publisher
                // and sender in each service's own host (they need a live
                // client), but the OSS ones read their connection from
                // configuration like the transports do, so they belong on the
                // same switch — one place answers "which brokers", for both
                // directions.
                services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
                services.AddSingleton<ICommandSender, RabbitMqCommandSender>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown {ProviderKey} '{provider}'. Supported: {AzureProvider}, {OssProvider}.");
        }

        return services;
    }
}
