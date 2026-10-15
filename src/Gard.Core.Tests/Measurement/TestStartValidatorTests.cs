using Gard.Core.Measurement;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Measurement;

public class TestStartValidatorTests
{
    private static TestStartBody Body(
        TestDirection direction = TestDirection.Down,
        double durationS = 10,
        int streams = 4,
        int payloadSize = 65_536,
        double warmupS = 1,
        BidirMode? bidirMode = null,
        double? gapS = null,
        TestTransport? transport = null) => new()
    {
        Direction = direction,
        DurationS = durationS,
        Streams = streams,
        PayloadSize = payloadSize,
        WarmupS = warmupS,
        BidirMode = bidirMode,
        GapS = gapS,
        Transport = transport,
    };

    [Fact]
    public void Accepts_TypicalTcpTest()
    {
        Assert.Null(TestStartValidator.Validate(Body(), Capabilities.DefaultV1));
    }

    [Fact]
    public void Accepts_TypicalUdpTest()
    {
        var body = Body(transport: TestTransport.Udp, payloadSize: 1200);
        Assert.Null(TestStartValidator.Validate(body, Capabilities.DefaultV1));
    }

    [Fact]
    public void Accepts_LegacyLsp10Body_WithoutOptionalFields()
    {
        Assert.Null(TestStartValidator.Validate(Body(streams: 1, warmupS: 0), negotiatedCaps: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(TestLimits.MaxStreams + 1)]
    [InlineData(50_000)]
    public void Rejects_StreamsOutOfRange(int streams)
    {
        var ex = TestStartValidator.Validate(Body(streams: streams), Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.TestRejected, ex?.Kind);
        Assert.Null(ex!.WireCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(TestLimits.MaxDurationS + 1)]
    [InlineData(1e9)]
    public void Rejects_DurationOutOfRange(double durationS)
    {
        var ex = TestStartValidator.Validate(Body(durationS: durationS), Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.TestRejected, ex?.Kind);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(TestLimits.MaxWarmupS + 1)]
    public void Rejects_WarmupOutOfRange(double warmupS)
    {
        var ex = TestStartValidator.Validate(Body(warmupS: warmupS), Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.TestRejected, ex?.Kind);
    }

    [Theory]
    [InlineData(-0.001)] // TimeSpan.FromSeconds(-0.001) = -1 ms = Task.Delay infinito
    [InlineData(TestLimits.MaxGapS + 1)]
    public void Rejects_GapOutOfRange(double gapS)
    {
        var body = Body(direction: TestDirection.Bidir, bidirMode: BidirMode.Sequential, gapS: gapS);
        var ex = TestStartValidator.Validate(body, Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.TestRejected, ex?.Kind);
    }

    [Fact]
    public void Rejects_UndefinedEnumValues()
    {
        Assert.NotNull(TestStartValidator.Validate(Body(direction: (TestDirection)9), Capabilities.DefaultV1));
        Assert.NotNull(TestStartValidator.Validate(Body(transport: (TestTransport)7), Capabilities.DefaultV1));
        Assert.NotNull(TestStartValidator.Validate(
            Body(direction: TestDirection.Bidir, bidirMode: (BidirMode)5), Capabilities.DefaultV1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TestLimits.MaxTcpPayloadSize + 1)]
    [InlineData(int.MaxValue)]
    public void Rejects_TcpPayloadOutOfRange_With3004(int payload)
    {
        var ex = TestStartValidator.Validate(Body(payloadSize: payload), Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.PayloadOutOfRange, ex?.Kind);
        Assert.Equal(3004, ex!.WireCode);
    }

    [Theory]
    [InlineData(UdpDataPlane.MinPayloadSize - 1)]
    [InlineData(UdpDataPlane.MaxPayloadSize + 1)]
    public void Rejects_UdpPayloadOutOfRange_With3004(int payload)
    {
        var body = Body(transport: TestTransport.Udp, payloadSize: payload);
        var ex = TestStartValidator.Validate(body, Capabilities.DefaultV1);
        Assert.Equal(3004, ex?.WireCode);
    }

    [Fact]
    public void Rejects_UdpWhenNotNegotiated_With3003()
    {
        var caps = Capabilities.DefaultV1 & ~Capabilities.UdpDataPlane;
        var ex = TestStartValidator.Validate(Body(transport: TestTransport.Udp, payloadSize: 1200), caps);
        Assert.Equal(LandspeedErrorKind.UdpNotNegotiated, ex?.Kind);
        Assert.Equal(3003, ex!.WireCode);
    }

    [Fact]
    public void Rejects_UdpSequentialBidir()
    {
        var body = Body(
            direction: TestDirection.Bidir, bidirMode: BidirMode.Sequential,
            transport: TestTransport.Udp, payloadSize: 1200);
        var ex = TestStartValidator.Validate(body, Capabilities.DefaultV1);
        Assert.Equal(LandspeedErrorKind.TestRejected, ex?.Kind);
    }

    [Fact]
    public void TestEndTimeout_CoversLongTests()
    {
        // Antes: 120 s fijos ⇒ todo test UDP ≥ 120 s fallaba en el host.
        Assert.True(TestLimits.TestEndTimeoutMs(Body(durationS: 150)) > 150_000);

        var max = Body(durationS: TestLimits.MaxDurationS, warmupS: TestLimits.MaxWarmupS);
        Assert.True(TestLimits.TestEndTimeoutMs(max) > (TestLimits.MaxDurationS + TestLimits.MaxWarmupS) * 1000);
    }
}
