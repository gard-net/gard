using System.Net;
using Gard.Core.Handshake;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Handshake;

public class PairingValidatorTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PairingPeer Peer(string ip) => new(IPAddress.Parse(ip), "peer", PeerPlatform.Linux);

    [Fact]
    public void GenerateCode_IsSixDigits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = CodePairingValidator.GenerateCode();
            Assert.True(CodePairingValidator.IsValidCode(code), code);
        }
    }

    [Theory]
    [InlineData("000000", true)]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("١٢٣٤٥٦", false)] // dígitos no ASCII
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidCode(string? code, bool expected)
    {
        Assert.Equal(expected, CodePairingValidator.IsValidCode(code));
    }

    [Fact]
    public void Constructor_RejectsMalformedCode()
    {
        Assert.Throws<ArgumentException>(() => new CodePairingValidator("12345"));
    }

    [Fact]
    public void Validate_AcceptsOnlyTheExactCode()
    {
        var v = new CodePairingValidator("004213");
        Assert.True(v.RequiresPairing(Peer("10.0.0.2")));
        Assert.Equal(PairingVerdict.Accepted, v.Validate(Peer("10.0.0.2"), "004213"));
        Assert.Equal(PairingVerdict.InvalidCode, v.Validate(Peer("10.0.0.2"), "4213"));
        Assert.Equal(PairingVerdict.InvalidCode, v.Validate(Peer("10.0.0.2"), "004214"));
        Assert.Equal(PairingVerdict.InvalidCode, v.Validate(Peer("10.0.0.2"), "0042130"));
    }

    [Fact]
    public void Validate_RateLimitsPerSource_EvenWithCorrectCode()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var v = new CodePairingValidator("111111", clock);

        for (var i = 0; i < CodePairingValidator.MaxFailuresPerSourcePerWindow; i++)
            Assert.Equal(PairingVerdict.InvalidCode, v.Validate(Peer("10.0.0.9"), "000000"));

        // Bloqueado: ni siquiera el código correcto pasa (no se evalúa).
        Assert.Equal(PairingVerdict.RateLimited, v.Validate(Peer("10.0.0.9"), "111111"));
        // Otro origen no se ve afectado.
        Assert.Equal(PairingVerdict.Accepted, v.Validate(Peer("10.0.0.10"), "111111"));

        // Pasada la ventana, el origen vuelve a poder intentar.
        clock.Now += CodePairingValidator.Window;
        Assert.Equal(PairingVerdict.Accepted, v.Validate(Peer("10.0.0.9"), "111111"));
    }

    [Fact]
    public void Validate_GroupsIpv6By64Prefix_AndMapsIpv4()
    {
        var v = new CodePairingValidator("222222", new ManualClock(DateTimeOffset.UnixEpoch));

        // Rotar la interface-id dentro del mismo /64 no da intentos nuevos.
        for (var i = 1; i <= CodePairingValidator.MaxFailuresPerSourcePerWindow; i++)
            v.Validate(Peer($"2001:db8:1:2::{i:x}"), "000000");
        Assert.Equal(PairingVerdict.RateLimited, v.Validate(Peer("2001:db8:1:2::ffff"), "222222"));
        Assert.Equal(PairingVerdict.Accepted, v.Validate(Peer("2001:db8:1:3::1"), "222222"));

        // IPv4 e IPv4-mapped-IPv6 son el mismo origen (socket dual-stack).
        for (var i = 0; i < CodePairingValidator.MaxFailuresPerSourcePerWindow; i++)
            v.Validate(Peer("192.168.1.7"), "000000");
        Assert.Equal(PairingVerdict.RateLimited, v.Validate(Peer("::ffff:192.168.1.7"), "222222"));
    }

    [Fact]
    public void Validate_GlobalLimit_BoundsDistributedBruteForce()
    {
        var v = new CodePairingValidator("333333", new ManualClock(DateTimeOffset.UnixEpoch));
        for (var i = 0; i < CodePairingValidator.MaxFailuresGlobalPerWindow; i++)
            Assert.Equal(PairingVerdict.InvalidCode, v.Validate(Peer($"10.1.{i}.1"), "000000"));

        Assert.Equal(PairingVerdict.RateLimited, v.Validate(Peer("10.2.0.1"), "333333"));
    }

    [Fact]
    public void AlwaysAccept_NeverRequiresPairing()
    {
        var v = new AlwaysAcceptPairing();
        Assert.False(v.RequiresPairing(Peer("10.0.0.1")));
        Assert.Equal(PairingVerdict.Accepted, v.Validate(Peer("10.0.0.1"), "x"));
    }
}
