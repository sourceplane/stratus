using Stratus.BuildingBlocks;
using Stratus.Tenancy.Domain;
using Xunit;

namespace Stratus.Tenancy.Tests;

/// <summary>
/// The aggregate is tested directly, with no service, no repository and no
/// container — an invariant that needs scaffolding to test is an invariant in
/// the wrong place.
/// </summary>
public class TenantTests
{
    private static readonly IClock Clock = new FixedClock();

    [Fact]
    public void Create_normalises_and_accepts_a_valid_slug()
    {
        var result = Tenant.Create("  Acme Inc  ", "acme-inc", Clock);

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Inc", result.Value.Name);
        Assert.Equal("acme-inc", result.Value.Slug);
    }

    [Theory]
    [InlineData("Acme_Inc")]
    [InlineData("ACME")]
    [InlineData("acme inc")]
    public void Create_rejects_a_slug_that_Azure_and_Coolify_would_reject(string slug)
    {
        // Caught in the domain rather than at deploy time, where the repo would
        // already exist under an unprovisionable name.
        var result = Tenant.Create("Acme", slug, Clock);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public void AddMember_refuses_a_duplicate_user()
    {
        var tenant = Tenant.Create("Acme", "acme", Clock).Value;
        var user = Guid.CreateVersion7();

        Assert.True(tenant.AddMember(user, "member").IsSuccess);
        var second = tenant.AddMember(user, "admin");

        Assert.False(second.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, second.Error.Kind);
        Assert.Single(tenant.Members);
    }

    [Fact]
    public void AddMember_refuses_an_unknown_role()
    {
        var tenant = Tenant.Create("Acme", "acme", Clock).Value;

        var result = tenant.AddMember(Guid.CreateVersion7(), "superuser");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Empty(tenant.Members);
    }

    [Fact]
    public void Members_are_exposed_read_only()
    {
        var tenant = Tenant.Create("Acme", "acme", Clock).Value;

        // The collection is reachable but not mutable from outside: membership
        // changes go through the aggregate so its rules cannot be bypassed.
        Assert.IsNotAssignableFrom<ICollection<Membership>>(tenant.Members);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
