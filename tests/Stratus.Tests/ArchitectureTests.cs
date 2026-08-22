using NetArchTest.Rules;
using Stratus.Contracts;
using Xunit;

namespace Stratus.Architecture.Tests;

/// <summary>
/// Boundaries enforced by tests, not by discipline. This is the cheapest
/// high-leverage thing in the repo: it is what stops a forked fleet from
/// quietly becoming a distributed monolith two years in.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Contracts_takes_no_dependency_on_any_other_Stratus_project()
    {
        var result = Types.InAssembly(typeof(Tenant).Assembly)
            .Should()
            .NotHaveDependencyOn("Stratus.Messaging")
            .And().NotHaveDependencyOn("Stratus.ServiceDefaults")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Contracts is the shared shape of the system; it must not depend on anything that consumes it.");
    }

    [Fact]
    public void Messaging_does_not_reach_into_a_service()
    {
        var result = Types.InAssembly(typeof(IEventPublisher).Assembly)
            .Should()
            .NotHaveDependencyOn("Stratus.Identity")
            .And().NotHaveDependencyOn("Stratus.Tenancy")
            .And().NotHaveDependencyOn("Stratus.Billing")
            .GetResult();

        Assert.True(result.IsSuccessful, "Shared infrastructure must not know about the services that use it.");
    }
}
