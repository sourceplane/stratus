using Microsoft.Extensions.Logging;
using Stratus.BuildingBlocks;
using Stratus.Contracts;

namespace Stratus.Projector.Application;

/// <summary>
/// One projector per read model, selected by event type. Adding a read model is
/// a new implementation registered in the composition root — the dispatch loop
/// never grows a switch statement. That is the Open/Closed Principle doing
/// actual work rather than appearing in a comment.
/// </summary>
public interface IProjection
{
    /// <summary>Event types this projection consumes.</summary>
    IReadOnlyCollection<string> Handles { get; }

    Task ApplyAsync(IntegrationEvent @event, CancellationToken ct = default);
}

public interface IProjectionDispatcher
{
    Task<Result<int>> DispatchAsync(IntegrationEvent @event, CancellationToken ct = default);
}

public sealed class ProjectionDispatcher(
    IEnumerable<IProjection> projections,
    ILogger<ProjectionDispatcher> logger) : IProjectionDispatcher
{
    public async Task<Result<int>> DispatchAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var interested = projections.Where(p => p.Handles.Contains(@event.Type)).ToList();
        if (interested.Count == 0)
        {
            // Not an error: a projector legitimately ignores most of the log,
            // and treating "nobody cared" as a failure would dead-letter the
            // majority of a healthy stream.
            ProjectorLog.NoProjectionHandles(logger, @event.Type);
            return Result<int>.Success(0);
        }

        foreach (var projection in interested)
        {
            await projection.ApplyAsync(@event, ct).ConfigureAwait(false);
        }

        return Result<int>.Success(interested.Count);
    }
}

/// <summary>A worked example: the tenant directory read model.</summary>
public sealed class TenantDirectoryProjection(ILogger<TenantDirectoryProjection> logger) : IProjection
{
    public IReadOnlyCollection<string> Handles { get; } =
        [EventTypes.TenantCreated, EventTypes.MemberAdded];

    public Task ApplyAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ProjectorLog.Projecting(logger, @event.Type, @event.TenantId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Source-generated logging. Both call sites are per-event, so both are hot
/// paths: the generator emits strongly-typed, allocation-free calls that skip
/// argument evaluation entirely when the level is disabled. That satisfies
/// CA1873 by construction rather than by suppression — a property access
/// boxed into a params array was being evaluated whether or not anyone was
/// listening, and LogDebug in particular is disabled in every environment
/// that matters.
/// </summary>
internal static partial class ProjectorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "No projection handles {Type}.")]
    public static partial void NoProjectionHandles(ILogger logger, string type);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Projecting {Type} for tenant {TenantId} into the tenant directory.")]
    public static partial void Projecting(ILogger logger, string type, Guid tenantId);
}
