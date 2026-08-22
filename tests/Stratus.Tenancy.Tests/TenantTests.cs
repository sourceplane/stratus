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
    public void Members_expose_no_mutator_on_their_compile_time_surface()
    {
        // The guarantee that matters is the DECLARED type: callers see
        // IReadOnlyCollection<Membership>, which has no Add or Remove, so
        // `tenant.Members.Add(...)` does not compile and membership changes
        // must go through the aggregate.
        //
        // Asserting on the runtime type instead would be asserting on the
        // wrong thing — ReadOnlyCollection<T> implements ICollection<T>
        // explicitly, by BCL design, so it is assignable to it no matter how
        // read-only the aggregate is.
        var property = typeof(Tenant).GetProperty(nameof(Tenant.Members));

        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyCollection<Membership>), property.PropertyType);
    }

    [Fact]
    public void Members_reject_mutation_through_a_cast()
    {
        var tenant = Tenant.Create("Acme", "acme", Clock).Value;
        var member = tenant.AddMember(Guid.CreateVersion7(), "owner").Value;

        // The compile-time surface is the first line of defence; this is the
        // second. A caller that casts its way to ICollection<T> must fail
        // loudly rather than quietly bypass the aggregate's rules.
        //
        // Only the public API is used to obtain a Membership: the constructor
        // seam is internal on purpose, and a test that needed InternalsVisibleTo
        // to reach it would be widening the aggregate's surface to test that
        // the surface is narrow.
        var escaped = (ICollection<Membership>)tenant.Members;

        // Statement lambdas with an explicit discard: Remove returns bool, and
        // handing xUnit a value-returning lambda selects the Func<object>
        // overload, which its analyzers reject.
        Assert.Throws<NotSupportedException>(() => { _ = escaped.Remove(member); });
        Assert.Throws<NotSupportedException>(() => escaped.Clear());
        Assert.Single(tenant.Members);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
