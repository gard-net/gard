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

    [Fact]
    public void PreferUsefulAddress_LanBeatsTailscaleEvenWhenBothAreLocal()
    {
        // hp-ia anunciaba 100.126.x (Tailscale) y 192.168.8.x; ambas /24 son
        // locales para el que escanea, y ganaba la primera en llegar.
        var selected = PeerAddressSelector.PreferUsefulAddress(
            [IPAddress.Parse("100.126.109.34"), IPAddress.Parse("192.168.8.101")],
            [IPAddress.Parse("100.126.109.20"), IPAddress.Parse("192.168.8.147")]);

        Assert.Equal(IPAddress.Parse("192.168.8.101"), selected);
    }

    [Fact]
    public void PreferUsefulAddress_LanBeatsLocalDockerBridge()
    {
        var selected = PeerAddressSelector.PreferUsefulAddress(
            [IPAddress.Parse("172.19.0.1"), IPAddress.Parse("192.168.8.105")],
            [IPAddress.Parse("172.19.0.5"), IPAddress.Parse("192.168.8.147")]);

        Assert.Equal(IPAddress.Parse("192.168.8.105"), selected);
    }

    [Fact]
    public void PreferUsefulAddress_CgnatStillUsedWhenItIsTheOnlyOption()
    {
        var selected = PeerAddressSelector.PreferUsefulAddress(
            [IPAddress.Parse("100.101.1.2")], [IPAddress.Parse("192.168.8.147")]);

        Assert.Equal(IPAddress.Parse("100.101.1.2"), selected);
    }
}
