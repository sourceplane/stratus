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
        LoggingChannelLog.Delivered(logger, recipient, subject);
        return Task.FromResult(Result<bool>.Success(true));
    }
}

/// <summary>
/// Source-generated logging, for consistency with every other call site in the
/// fleet. The arguments here are plain parameters and would not trip CA1873 on
/// their own, but one logging idiom is worth more than a per-site exemption:
/// the next edit that swaps a parameter for a property access should not
/// silently reintroduce the defect.
/// </summary>
internal static partial class LoggingChannelLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[notification] to={Recipient} subject={Subject}")]
    public static partial void Delivered(ILogger logger, string recipient, string subject);
}
