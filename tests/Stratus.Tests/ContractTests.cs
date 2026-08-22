using Stratus.Contracts;
using Xunit;

namespace Stratus.Identity.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Event_envelope_carries_the_tenant_as_its_partition_key()
    {
        var tenantId = Guid.CreateVersion7();
        var envelope = new EventEnvelope(
            Guid.CreateVersion7(), EventTypes.UserRegistered, tenantId, DateTimeOffset.UtcNow, 1, "{}");

        Assert.Equal(tenantId, envelope.TenantId);
        Assert.Equal(1, envelope.Version);
    }

    [Fact]
    public void Event_types_are_context_entity_verb()
    {
        Assert.Equal(3, EventTypes.UserRegistered.Split('.').Length);
        Assert.Equal(3, EventTypes.PlanChanged.Split('.').Length);
    }
}
