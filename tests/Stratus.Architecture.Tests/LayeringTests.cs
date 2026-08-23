using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Stratus.Architecture.Tests;

/// <summary>
/// The layering is a claim until something checks it. These tests are what
/// stop a forked fleet from becoming a distributed monolith: a project
/// reference added in the wrong direction fails here, at PR time, rather than
/// being discovered years later when nothing can be extracted.
/// </summary>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(Identity.Domain.User).Assembly;
    private static readonly Assembly Application = typeof(Identity.Application.IUserService).Assembly;
    private static readonly Assembly Infrastructure = typeof(Identity.Infrastructure.IdentityDbContext).Assembly;
    private static readonly Assembly Web = typeof(Identity.Web.Controllers.UsersController).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_outward()
    {
        var result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny(
                "Stratus.Identity.Application",
                "Stratus.Identity.Infrastructure",
                "Stratus.Identity.Web",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Domain must not know about outer layers or infrastructure. Offenders: {Names(result)}");
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_Web()
    {
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny(
                "Stratus.Identity.Infrastructure",
                "Stratus.Identity.Web",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"Application declares interfaces; Infrastructure implements them, never the reverse. Offenders: {Names(result)}");
    }

    [Fact]
    public void Web_cannot_reach_Infrastructure()
    {
        var result = Types.InAssembly(Web)
            .Should()
            .NotHaveDependencyOnAny("Stratus.Identity.Infrastructure", "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"A controller must not be able to touch a DbContext. Offenders: {Names(result)}");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Web()
    {
        var result = Types.InAssembly(Infrastructure)
            .Should()
            .NotHaveDependencyOn("Stratus.Identity.Web")
            .GetResult();

        Assert.True(result.IsSuccessful, $"Infrastructure serves Application, not the web layer. Offenders: {Names(result)}");
    }

    [Fact]
    public void Bounded_contexts_do_not_reference_each_other_directly()
    {
        // Cross-context communication is HTTP or the event log, never a project
        // reference — otherwise "microservices" is a deployment detail of a
        // monolith.
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Web })
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny("Stratus.Tenancy", "Stratus.Billing", "Stratus.Notifier", "Stratus.Projector")
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{assembly.GetName().Name} reaches into another bounded context. Offenders: {Names(result)}");
        }
    }

    [Fact]
    public void Every_domain_assembly_obeys_the_same_rule()
    {
        foreach (var domain in new[] { Domain, typeof(Tenancy.Domain.Tenant).Assembly, typeof(Billing.Domain.Subscription).Assembly })
        {
            var result = Types.InAssembly(domain)
                .Should()
                .NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Azure.Messaging")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{domain.GetName().Name} must stay persistence-ignorant. Offenders: {Names(result)}");
        }
    }

    /// <summary>
    /// Stratus.Messaging is the ONLY project allowed to name a broker SDK.
    ///
    /// This rule is here because its absence let the leak happen. The domain
    /// rule above already forbade Azure.Messaging — but only in Domain, so the
    /// projector's and notifier's INFRASTRUCTURE layers each constructed an
    /// Azure client directly and nothing objected. Publishing went through
    /// IEventPublisher; consuming named a cloud in two service projects.
    ///
    /// The point is not tidiness. A baseline that has to run against a second
    /// set of brokers can only do so if "which broker" is answered in one
    /// place, and a rule that is not enforced is a rule that decays back to
    /// where it started.
    /// </summary>
    [Fact]
    public void Only_the_messaging_package_may_name_a_broker_sdk()
    {
        var brokerSdks = new[] { "Azure.Messaging", "Azure.Identity", "Confluent.Kafka", "RabbitMQ.Client" };

        var assemblies = new[]
        {
            Domain, Application, Infrastructure, Web,
            typeof(Tenancy.Domain.Tenant).Assembly,
            typeof(Billing.Domain.Subscription).Assembly,
            typeof(Stratus.Projector.Infrastructure.EventConsumer).Assembly,
            typeof(Stratus.Notifier.Infrastructure.CommandConsumer).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(brokerSdks)
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{assembly.GetName().Name} names a broker SDK directly — transports belong behind "
                + $"IEventTransport/ICommandTransport in Stratus.Messaging. Offenders: {Names(result)}");
        }
    }

    private static string Names(TestResult result) =>
        result.FailingTypeNames is null ? "none reported" : string.Join(", ", result.FailingTypeNames);
}
