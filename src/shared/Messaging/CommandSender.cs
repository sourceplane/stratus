using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Stratus.Contracts;

namespace Stratus.Messaging;

/// <summary>Sends commands — one owner, an expectation of completion.</summary>
public interface ICommandSender
{
    Task SendAsync(CommandEnvelope command, CancellationToken ct = default);
}

public sealed class ServiceBusCommandSender(ServiceBusSender sender) : ICommandSender
{
    public async Task SendAsync(CommandEnvelope command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = new ServiceBusMessage(JsonSerializer.Serialize(command))
        {
            // MessageId drives Service Bus duplicate detection; SessionId keeps
            // a tenant's commands in order without serialising every tenant.
            MessageId = command.CommandId.ToString(),
            SessionId = command.TenantId.ToString(),
            Subject = command.Type,
        };

        await sender.SendMessageAsync(message, ct).ConfigureAwait(false);
    }
}
