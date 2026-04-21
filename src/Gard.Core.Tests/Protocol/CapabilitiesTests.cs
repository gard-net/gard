using Gard.Core.Protocol;

namespace Gard.Core.Tests.Protocol;

public class CapabilitiesTests
{
    [Fact]
    public void HexRoundTrip()
    {
        var caps = Capabilities.ParallelStreams | Capabilities.Tls | Capabilities.Pairing;
        var hex = caps.ToHexString();
        Assert.Equal(caps, CapabilitiesExtensions.ParseHex(hex));
    }

    [Fact]
    public void HexWithPrefix()
    {
        Assert.Equal((Capabilities)0x0D, CapabilitiesExtensions.ParseHex("0x0d"));
    }

    [Fact]
    public void InvalidHexReturnsNull()
    {
        Assert.Null(CapabilitiesExtensions.ParseHex(""));
        Assert.Null(CapabilitiesExtensions.ParseHex("zz"));
    }

    [Fact]
    public void DefaultV1HasExpectedBits()
    {
        var c = Capabilities.DefaultV1;
        Assert.True(c.HasFlag(Capabilities.ParallelStreams));
        Assert.True(c.HasFlag(Capabilities.Bidirectional));
        Assert.True(c.HasFlag(Capabilities.Pairing));
        Assert.True(c.HasFlag(Capabilities.ClockSync));
        Assert.True(c.HasFlag(Capabilities.DataEcho));
        Assert.True(c.HasFlag(Capabilities.BidirSequential));
        Assert.True(c.HasFlag(Capabilities.IntervalReporting));
        Assert.True(c.HasFlag(Capabilities.UdpDataPlane));
        Assert.False(c.HasFlag(Capabilities.Tls));
    }

    [Fact]
    public void DefaultV1_BackwardsCompatV10()
    {
        var v10 = Capabilities.ParallelStreams | Capabilities.Bidirectional |
                  Capabilities.Pairing | Capabilities.ClockSync | Capabilities.DataEcho;
        var common = Capabilities.DefaultV1.Intersection(v10);
        Assert.Equal(v10, common);
        Assert.False(common.HasFlag(Capabilities.BidirSequential));
        Assert.False(common.HasFlag(Capabilities.IntervalReporting));
    }

    [Fact]
    public void IntersectionSubset()
    {
        var a = Capabilities.ParallelStreams | Capabilities.Tls | Capabilities.Pairing | Capabilities.ClockSync;
        var b = Capabilities.ParallelStreams | Capabilities.Pairing | Capabilities.DataEcho;
        Assert.Equal(Capabilities.ParallelStreams | Capabilities.Pairing, a.Intersection(b));
    }

    [Fact]
    public void DefaultV1_HasExactHexValue()
    {
        // 0x1FB = todos los bits salvo TLS (0x04), incluye UdpDataPlane (0x100):
        // ParallelStreams(0x01) | Bidirectional(0x02) | Pairing(0x08) | ClockSync(0x10)
        //  | DataEcho(0x20) | BidirSequential(0x40) | IntervalReporting(0x80)
        //  | UdpDataPlane(0x100)
        Assert.Equal("1fb", Capabilities.DefaultV1.ToHexString());
    }
}

public class ProtocolVersionTests
{
    [Fact]
    public void Parse()
    {
        Assert.Equal(new ProtocolVersion(1, 0), ProtocolVersion.Parse("1.0"));
        Assert.Equal(new ProtocolVersion(2, 0), ProtocolVersion.Parse("2"));
        Assert.Null(ProtocolVersion.Parse("abc"));
        Assert.Null(ProtocolVersion.Parse("1.2.3"));
    }

    [Fact]
    public void Compatibility()
    {
        var v10 = new ProtocolVersion(1, 0);
        var v12 = new ProtocolVersion(1, 2);
        var v20 = new ProtocolVersion(2, 0);
        Assert.True(v10.IsCompatibleWith(v12));
        Assert.False(v10.IsCompatibleWith(v20));
    }

    [Fact]
    public void CodableAsString()
    {
        var v = new ProtocolVersion(1, 3);
        var json = System.Text.Json.JsonSerializer.Serialize(v);
        Assert.Equal("\"1.3\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<ProtocolVersion>(json);
        Assert.Equal(v, back);
    }

    [Fact]
    public void CurrentIsOneDotTwo()
    {
        Assert.Equal(new ProtocolVersion(1, 2), ProtocolVersion.Current);
    }
}
