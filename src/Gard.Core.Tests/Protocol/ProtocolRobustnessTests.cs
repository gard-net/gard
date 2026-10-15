using System.Text;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Protocol;

public class ProtocolRobustnessTests
{
    [Fact]
    public void Parser_ManySmallFramesInOneRead_AllExtracted()
    {
        const int count = 20_000;
        var heartbeat = FrameCodec.Encode(new Frame(FrameType.Heartbeat));
        var wire = new byte[heartbeat.Length * count];
        for (var i = 0; i < count; i++) heartbeat.CopyTo(wire, i * heartbeat.Length);

        var parser = new FrameStreamParser();
        var frames = parser.Feed(wire);

        Assert.Equal(count, frames.Count);
        Assert.All(frames, f => Assert.Equal(FrameType.Heartbeat, f.Type));
        Assert.Equal(0, parser.PendingBytes);
    }

    [Fact]
    public void Parser_KeepsPartialTailAcrossFeeds()
    {
        var a = FrameCodec.Encode(new Frame(FrameType.ControlJson, Encoding.UTF8.GetBytes("{\"a\":1}")));
        var b = FrameCodec.Encode(new Frame(FrameType.ControlJson, Encoding.UTF8.GetBytes("{\"b\":2}")));
        var joined = a.Concat(b).ToArray();
        var cut = a.Length + 3;

        var parser = new FrameStreamParser();
        var first = parser.Feed(joined.AsSpan(0, cut));
        Assert.Single(first);
        Assert.Equal(3, parser.PendingBytes);

        var second = parser.Feed(joined.AsSpan(cut));
        Assert.Single(second);
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(second[0].Payload.Span));
    }

    [Fact]
    public void Parser_ReleasesLargeBuffer_AfterBigFrame()
    {
        var big = FrameCodec.Encode(new Frame(FrameType.DataBinary, new byte[2 * 1024 * 1024]));
        var parser = new FrameStreamParser();

        Assert.Single(parser.Feed(big));
        Assert.True(parser.BufferCapacity <= 4096, $"capacity={parser.BufferCapacity}");
    }

    [Fact]
    public void Parser_AcceptsExactDefaultLimit_RejectsOneMore()
    {
        var parser = new FrameStreamParser();
        var header = new byte[4];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, FrameCodec.MaxFrameSize + 1u);
        var ex = Assert.Throws<LandspeedException>(() => new FrameStreamParser().Feed(header));
        Assert.Equal(LandspeedErrorKind.FrameTooLarge, ex.Kind);

        // Exactamente el límite: no lanza al ver el header, espera el resto.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, FrameCodec.MaxFrameSize);
        Assert.Empty(parser.Feed(header));
    }

    [Theory]
    [InlineData("1", 1, 0)]
    [InlineData("1.2", 1, 2)]
    [InlineData("1.2.0", 1, 2)]
    [InlineData("1.3.7-beta.1", 1, 3)]
    [InlineData("2.0.0+build", 2, 0)]
    public void ProtocolVersion_ParsesSemVer(string text, int major, int minor)
    {
        Assert.Equal(new ProtocolVersion(major, minor), ProtocolVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("1.x")]
    [InlineData("1.2.")]
    [InlineData("-1.2")]
    [InlineData("1.2.beta")]
    public void ProtocolVersion_RejectsGarbage(string text)
    {
        Assert.Null(ProtocolVersion.Parse(text));
    }

    [Fact]
    public void Hello_WithThreePartVersion_Decodes()
    {
        var hello = new HelloMessage(1, new HelloBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            AppVersion = "port-1.0",
            Platform = PeerPlatform.Linux,
            DeviceName = "port",
            Caps = Capabilities.DefaultV1,
            Nonce = "AAAA",
        });
        var json = Encoding.UTF8.GetString(ControlMessageCodec.Encode(hello))
            .Replace("\"1.2\"", "\"1.2.0\"");

        var decoded = Assert.IsType<HelloMessage>(ControlMessageCodec.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(new ProtocolVersion(1, 2), decoded.Body.ProtocolVersion);
    }

    [Fact]
    public void TestStart_WithIntegerEnum_IsMalformed()
    {
        var start = new TestStartMessage(1, new TestStartBody
        {
            Direction = TestDirection.Down,
            DurationS = 1,
            Streams = 1,
            PayloadSize = 1024,
            WarmupS = 0,
        });
        var json = Encoding.UTF8.GetString(ControlMessageCodec.Encode(start));
        Assert.Contains("\"down\"", json);

        var tampered = json.Replace("\"down\"", "9");
        var ex = Assert.Throws<LandspeedException>(() => ControlMessageCodec.Decode(Encoding.UTF8.GetBytes(tampered)));
        Assert.Equal(LandspeedErrorKind.MalformedControlJson, ex.Kind);
    }
}
