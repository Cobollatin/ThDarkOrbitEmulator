using ThDarkOrbitEmulator.AppHost.ClientTesting;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class ClientTestingLocalGuardTests
{
    [Test]
    public void EnsureAllowed_ThrowsWhenDisabled()
    {
        var options = new ClientTestingOptions { Enabled = false };
        Assert.Throws<InvalidOperationException>(() => ClientTestingLocalGuard.EnsureAllowed(options));
    }
}
