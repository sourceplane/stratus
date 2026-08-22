using Microsoft.Extensions.Logging.Abstractions;
using Stratus.Contracts;
using Stratus.Projector.Application;
using Xunit;

namespace Stratus.Projector.Tests;

/// <summary>
/// The dispatcher's whole job is selection: which read models care about this
/// event. That rule is pure, so it is tested with fakes and no Event Hubs —
/// which is the payoff for putting the broker behind Infrastructure.
/// </summary>
public class ProjectionDispatcherTests
{
    private static IntegrationEvent AnEvent(string type) =>
        new(Guid.CreateVersion7(), type, Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, 1, "{}");

    [Fact]
    public async Task Every_interested_projection_receives_the_event()
    {
        var first = new RecordingProjection(EventTypes.TenantCreated);
        var second = new RecordingProjection(EventTypes.TenantCreated);
        var dispatcher = new ProjectionDispatcher([first, second], NullLogger<ProjectionDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(AnEvent(EventTypes.TenantCreated));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value);
        Assert.Equal(1, first.Applied);
        Assert.Equal(1, second.Applied);
    }

    /// <summary>
    /// The Open/Closed claim, asserted rather than commented: a projection the
    /// dispatcher has never heard of takes part purely by being registered, and
    /// one that does not handle the type is left alone.
    /// </summary>
    [Fact]
    public async Task A_projection_that_does_not_handle_the_type_is_not_called()
    {
        var interested = new RecordingProjection(EventTypes.PlanChanged);
        var uninterested = new RecordingProjection(EventTypes.UserRegistered);
        var dispatcher = new ProjectionDispatcher(
            [interested, uninterested], NullLogger<ProjectionDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(AnEvent(EventTypes.PlanChanged));

        Assert.Equal(1, result.Value);
        Assert.Equal(1, interested.Applied);
        Assert.Equal(0, uninterested.Applied);
    }

    /// <summary>
    /// Not an error. A projector legitimately ignores most of the log, and
    /// treating "nobody cared" as a failure would dead-letter the majority of a
    /// healthy stream.
    /// </summary>
    [Fact]
    public async Task An_event_nobody_handles_succeeds_with_a_count_of_zero()
    {
        var dispatcher = new ProjectionDispatcher(
            [new RecordingProjection(EventTypes.UserRegistered)],
            NullLogger<ProjectionDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(AnEvent(EventTypes.PlanChanged));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task A_projection_handling_several_types_receives_each_of_them()
    {
        var projection = new RecordingProjection(EventTypes.TenantCreated, EventTypes.MemberAdded);
        var dispatcher = new ProjectionDispatcher([projection], NullLogger<ProjectionDispatcher>.Instance);

        await dispatcher.DispatchAsync(AnEvent(EventTypes.TenantCreated));
        await dispatcher.DispatchAsync(AnEvent(EventTypes.MemberAdded));

        Assert.Equal(2, projection.Applied);
    }

    [Fact]
    public async Task A_null_event_is_rejected_before_any_projection_runs()
    {
        var projection = new RecordingProjection(EventTypes.TenantCreated);
        var dispatcher = new ProjectionDispatcher([projection], NullLogger<ProjectionDispatcher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!));
        Assert.Equal(0, projection.Applied);
    }

    private sealed class RecordingProjection(params string[] handles) : IProjection
    {
        public IReadOnlyCollection<string> Handles { get; } = handles;

        public int Applied { get; private set; }

        public Task ApplyAsync(IntegrationEvent @event, CancellationToken ct = default)
        {
            Applied++;
            return Task.CompletedTask;
        }
    }
}
