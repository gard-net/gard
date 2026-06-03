using System.Net;
using Gard.Core.Discovery;

namespace Gard.Core.Tests.Discovery;

public class PeerAddressSelectorTests
{
    [Fact]
    public void PreferUsefulAddress_PrefersSameLanOverDockerBridge()
    {
        var selected = PeerAddressSelector.PreferUsefulAddress(
            [
                IPAddress.Parse("172.19.0.1"),
                IPAddress.Parse("192.168.8.105"),
            ],
            [IPAddress.Parse("192.168.8.147")]);

        Assert.Equal(IPAddress.Parse("192.168.8.105"), selected);
    }

    [Fact]
    public void PreferUsefulAddress_IgnoresLoopbackAndLinkLocal()
    {
        var selected = PeerAddressSelector.PreferUsefulAddress(
            [
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("169.254.10.2"),
                IPAddress.Parse("10.0.0.5"),
            ],
            [IPAddress.Parse("192.168.8.147")]);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), selected);
    }
}
