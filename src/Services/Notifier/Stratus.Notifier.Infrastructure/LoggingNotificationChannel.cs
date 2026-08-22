using Microsoft.Extensions.Logging;
using Stratus.BuildingBlocks;
using Stratus.Notifier.Application;

namespace Stratus.Notifier.Infrastructure;

/// <summary>
/// The development channel. A real provider (SES, Resend, Postmark) implements
/// the same port and is swapped in the composition root — no caller changes.
/// </summary>
public sealed class LoggingNotificationChannel(ILogger<LoggingNotificationChannel> logger) : INotificationChannel
{
    public Task<Result<bool>> DeliverAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        logger.LogInformation("[notification] to={Recipient} subject={Subject}", recipient, subject);
        return Task.FromResult(Result<bool>.Success(true));
    }
}
