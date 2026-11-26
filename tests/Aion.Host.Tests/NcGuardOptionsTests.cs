using Aion.Host.NcGuard;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NcGuardOptionsTests
{
    /// <summary>A loopback research host keeps recording requests by default.</summary>
    [Fact]
    public void Loopback_host_keeps_capture_on_by_default()
    {
        Assert.True(new NcGuardOptions().ForBind(loopbackOnly: true, captureConfigured: false).Capture);
    }

    /// <summary>A host reachable by players records nothing unless capture was set on purpose.</summary>
    [Fact]
    public void Public_host_turns_capture_off_unless_configured()
    {
        Assert.False(new NcGuardOptions().ForBind(loopbackOnly: false, captureConfigured: false).Capture);
        Assert.True(new NcGuardOptions { Capture = true }.ForBind(loopbackOnly: false, captureConfigured: true).Capture);
        Assert.False(new NcGuardOptions { Capture = false }.ForBind(loopbackOnly: true, captureConfigured: true).Capture);
    }
}
