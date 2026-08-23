using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratus.Messaging;
using Xunit;

namespace Stratus.Architecture.Tests;

/// <summary>
/// Transport selection.
///
/// The transports themselves need brokers to exercise, so what is testable
/// here — and worth testing — is the DECISION: which pair a given
/// configuration resolves to, and what happens when the configuration is
/// wrong. That is the part a deployment gets wrong, and the part whose failure
/// mode is silent.
/// </summary>
public class MessagingSelectionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMessagingTransports(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_to_azure_so_an_existing_deployment_is_unchanged()
    {
        // Every deployment that exists today sets nothing. If the default
        // moved, they would all silently repoint at brokers that are not there.
        using var provider = Build();

        Assert.IsType<EventHubsEventTransport>(provider.GetRequiredService<IEventTransport>());
        Assert.IsType<ServiceBusCommandTransport>(provider.GetRequiredService<ICommandTransport>());
    }

    [Fact]
    public void Selects_the_self_hosted_pair_for_oss()
    {
        using var provider = Build((MessagingRegistration.ProviderKey, "oss"));

        Assert.IsType<KafkaEventTransport>(provider.GetRequiredService<IEventTransport>());
        Assert.IsType<RabbitMqCommandTransport>(provider.GetRequiredService<ICommandTransport>());
    }

    [Fact]
    public void The_oss_lane_registers_both_directions()
    {
        // A lane that consumed from Kafka but still published to Event Hubs
        // would look healthy and move nothing.
        using var provider = Build((MessagingRegistration.ProviderKey, "oss"));

        Assert.IsType<KafkaEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.IsType<RabbitMqCommandSender>(provider.GetRequiredService<ICommandSender>());
    }

    [Theory]
    [InlineData("Azure")]
    [InlineData("  oss  ")]
    [InlineData("OSS")]
    public void Is_forgiving_about_case_and_whitespace(string value)
    {
        // A config value typed by a human, or pasted from a terraform output
        // with a trailing newline, should not decide the messaging plane.
        using var provider = Build((MessagingRegistration.ProviderKey, value));
        Assert.NotNull(provider.GetRequiredService<IEventTransport>());
    }

    [Theory]
    [InlineData("kafka")]
    [InlineData("rabbit")]
    [InlineData("aws")]
    [InlineData("azur")]
    public void Throws_on_an_unknown_provider_rather_than_falling_back(string value)
    {
        // The failure this prevents: a typo selects the default, the fleet
        // boots, every health check passes, and nothing consumes anything —
        // because the services are connected to brokers that hold no traffic.
        // Failing at startup names the value and the alternatives.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Build((MessagingRegistration.ProviderKey, value)));

        Assert.Contains(value.ToLowerInvariant(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("azure", ex.Message, StringComparison.Ordinal);
        Assert.Contains("oss", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_value_is_absent_not_invalid()
    {
        // An unset environment variable frequently arrives as "" rather than
        // as nothing at all. That is "I did not choose", not "I chose wrongly".
        using var provider = Build((MessagingRegistration.ProviderKey, ""));
        Assert.IsType<EventHubsEventTransport>(provider.GetRequiredService<IEventTransport>());
    }
}
