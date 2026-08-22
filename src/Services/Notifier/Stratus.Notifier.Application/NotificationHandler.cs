using Microsoft.Extensions.Logging;
using Stratus.BuildingBlocks;
using Stratus.Contracts;

namespace Stratus.Notifier.Application;

/// <summary>
/// Delivery is a port, not a hard-coded provider. Swapping email vendors is an
/// Infrastructure change and touches nothing here.
/// </summary>
public interface INotificationChannel
{
    Task<Result<bool>> DeliverAsync(string recipient, string subject, string body, CancellationToken ct = default);
}

public interface INotificationHandler
{
    Task<Result<bool>> HandleAsync(IntegrationCommand command, CancellationToken ct = default);
}

public sealed class NotificationHandler(
    INotificationChannel channel,
    ILogger<NotificationHandler> logger) : INotificationHandler
{
    public async Task<Result<bool>> HandleAsync(IntegrationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!string.Equals(command.Type, CommandTypes.SendNotification, StringComparison.Ordinal))
        {
            // Not ours. Reported rather than thrown: an unexpected type on the
            // queue is an operational fact, not a crash.
            return Error.Validation($"Unsupported command type '{command.Type}'.");
        }

        logger.LogInformation("Delivering notification for tenant {TenantId}.", command.TenantId);
        return await channel.DeliverAsync(
            recipient: command.TenantId.ToString(),
            subject: "Stratus notification",
            body: command.Payload,
            ct).ConfigureAwait(false);
    }
}
