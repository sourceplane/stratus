using Stratus.Billing.Domain;
using Stratus.BuildingBlocks;
using Xunit;

namespace Stratus.Billing.Tests;

public class SubscriptionTests
{
    private static readonly IClock Clock = new FixedClock();

    [Fact]
    public void A_new_subscription_starts_on_free()
    {
        var subscription = Subscription.StartFree(Guid.CreateVersion7(), Clock);

        Assert.Equal(Plan.Free.Code, subscription.PlanCode);
        Assert.Same(Plan.Free, subscription.Plan);
    }

    [Fact]
    public void Changing_to_the_current_plan_is_a_conflict()
    {
        var subscription = Subscription.StartFree(Guid.CreateVersion7(), Clock);

        var result = subscription.ChangeTo("free", Clock);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    [Fact]
    public void Changing_to_an_unknown_plan_is_a_validation_failure()
    {
        var subscription = Subscription.StartFree(Guid.CreateVersion7(), Clock);

        var result = subscription.ChangeTo("platinum", Clock);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(Plan.Free.Code, subscription.PlanCode);
    }

    [Fact]
    public void Plan_codes_are_matched_case_insensitively()
    {
        var subscription = Subscription.StartFree(Guid.CreateVersion7(), Clock);

        Assert.True(subscription.ChangeTo("TEAM", Clock).IsSuccess);
        Assert.Equal(Plan.Team.Code, subscription.PlanCode);
    }

    [Theory]
    [InlineData("free", "sso", false)]
    [InlineData("enterprise", "sso", true)]
    [InlineData("free", "core.projects", true)]
    public void Entitlements_are_answered_by_the_plan_itself(string code, string feature, bool expected)
    {
        // One implementation of "is this allowed", not one per calling service.
        var plan = Plan.FromCode(code).Value;

        Assert.Equal(expected, plan.Allows(feature));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
