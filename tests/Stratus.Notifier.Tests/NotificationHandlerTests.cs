using Microsoft.Extensions.Logging.Abstractions;
using Stratus.BuildingBlocks;
using Stratus.Contracts;
using Stratus.Notifier.Application;
using Xunit;

namespace Stratus.Notifier.Tests;

/// <summary>
/// Delivery is a port, so the handler's rules are testable with a fake channel
/// and no Service Bus. Swapping email vendors is an Infrastructure change and
/// changes nothing here — which is the claim these tests hold to.
/// </summary>
public class NotificationHandlerTests
{
    private static IntegrationCommand ACommand(string type, Guid? tenantId = null) =>
        new(Guid.CreateVersion7(), type, tenantId ?? Guid.CreateVersion7(), "{\"body\":\"hello\"}");

    [Fact]
    public async Task A_send_notification_command_reaches_the_channel()
    {
        var channel = new RecordingChannel();
        var handler = new NotificationHandler(channel, NullLogger<NotificationHandler>.Instance);
        var command = ACommand(CommandTypes.SendNotification);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, channel.Delivered);
        Assert.Equal(command.TenantId.ToString(), channel.LastRecipient);
        Assert.Equal(command.Payload, channel.LastBody);
    }

    /// <summary>
    /// Reported, not thrown: an unexpected type on the queue is an operational
    /// fact. The consumer dead-letters on the Error rather than on an exception
    /// escaping, so the distinction is load-bearing.
    /// </summary>
    [Fact]
    public async Task An_unsupported_command_type_is_a_validation_failure_not_an_exception()
    {
        var channel = new RecordingChannel();
        var handler = new NotificationHandler(channel, NullLogger<NotificationHandler>.Instance);

        var result = await handler.HandleAsync(ACommand("billing.invoice.issue"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(0, channel.Delivered);
    }

    /// <summary>
    /// Ordinal comparison: a type differing only in case is a different type,
    /// not a near-miss to be accepted.
    /// </summary>
    [Fact]
    public async Task Command_type_matching_is_case_sensitive()
    {
        var channel = new RecordingChannel();
        var handler = new NotificationHandler(channel, NullLogger<NotificationHandler>.Instance);

        var result = await handler.HandleAsync(ACommand(CommandTypes.SendNotification.ToUpperInvariant()));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, channel.Delivered);
    }

    [Fact]
    public async Task A_channel_failure_is_returned_to_the_caller()
    {
        var handler = new NotificationHandler(
            new FailingChannel(), NullLogger<NotificationHandler>.Instance);

        var result = await handler.HandleAsync(ACommand(CommandTypes.SendNotification));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    [Fact]
    public async Task A_null_command_is_rejected_before_the_channel_is_touched()
    {
        var channel = new RecordingChannel();
        var handler = new NotificationHandler(channel, NullLogger<NotificationHandler>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!));
        Assert.Equal(0, channel.Delivered);
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public int Delivered { get; private set; }

        public string? LastRecipient { get; private set; }

        public string? LastBody { get; private set; }

        public Task<Result<bool>> DeliverAsync(
            string recipient, string subject, string body, CancellationToken ct = default)
        {
            Delivered++;
            LastRecipient = recipient;
            LastBody = body;
            return Task.FromResult(Result<bool>.Success(true));
        }
    }

    private sealed class FailingChannel : INotificationChannel
    {
        public Task<Result<bool>> DeliverAsync(
            string recipient, string subject, string body, CancellationToken ct = default) =>
            Task.FromResult(Result<bool>.Failure(Error.Conflict("the provider rejected the message")));
    }
}
