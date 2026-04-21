using Garc.Core.Protocol;

namespace Garc.Core.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public void Encode_EmptyHeartbeat()
    {
        var frame = new Frame(FrameType.Heartbeat);
        var wire = FrameCodec.Encode(frame);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x03 }, wire);
    }

    [Fact]
    public void Encode_ControlJsonWithPayload()
    {
        var payload = new byte[] { 0x7B, 0x7D };
        var frame = new Frame(FrameType.ControlJson, payload);
        var wire = FrameCodec.Encode(frame);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03, 0x01, 0x7B, 0x7D }, wire);
    }

    [Fact]
    public void Encode_TooLarge_Throws()
    {
        var payload = new byte[FrameCodec.MaxFrameSize];
        var frame = new Frame(FrameType.DataBinary, payload);
        var ex = Assert.Throws<LandspeedException>(() => FrameCodec.Encode(frame));
        Assert.Equal(LandspeedErrorKind.FrameTooLarge, ex.Kind);
    }

    [Fact]
    public void RoundTrip_DataBinary()
    {
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        var frame = new Frame(FrameType.DataBinary, payload);
        var wire = FrameCodec.Encode(frame);
        var parser = new FrameStreamParser();
        var frames = parser.Feed(wire);
        Assert.Single(frames);
        Assert.Equal(frame, frames[0]);
        Assert.Equal(0, parser.PendingBytes);
    }

    [Fact]
    public void RoundTrip_MultipleFramesInOneChunk()
    {
        var a = new Frame(FrameType.Heartbeat);
        var b = new Frame(FrameType.ControlJson, "{}"u8.ToArray());
        var c = new Frame(FrameType.DataBinaryEcho, new byte[] { 0xAA, 0xBB, 0xCC });
        var wire = FrameCodec.Encode(a).Concat(FrameCodec.Encode(b)).Concat(FrameCodec.Encode(c)).ToArray();
        var parser = new FrameStreamParser();
        var frames = parser.Feed(wire);
        Assert.Equal(3, frames.Count);
        Assert.Equal(a, frames[0]);
        Assert.Equal(b, frames[1]);
        Assert.Equal(c, frames[2]);
        Assert.Equal(0, parser.PendingBytes);
    }

    [Fact]
    public void Parser_ByteByByteDelivery()
    {
        var frame = new Frame(FrameType.ControlJson, "hola"u8.ToArray());
        var wire = FrameCodec.Encode(frame);
        var parser = new FrameStreamParser();
        var collected = new List<Frame>();
        foreach (var b in wire)
        {
            collected.AddRange(parser.Feed(new[] { b }));
        }
        Assert.Single(collected);
        Assert.Equal(frame, collected[0]);
    }

    [Fact]
    public void Parser_SplitInsideHeader()
    {
        var frame = new Frame(FrameType.DataBinary, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var wire = FrameCodec.Encode(frame);
        var parser = new FrameStreamParser();
        Assert.Empty(parser.Feed(wire.AsSpan(0, 2)));
        var rest = parser.Feed(wire.AsSpan(2));
        Assert.Single(rest);
        Assert.Equal(frame, rest[0]);
    }

    [Fact]
    public void Parser_RejectsLengthZero()
    {
        var parser = new FrameStreamParser();
        var bad = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        var ex = Assert.Throws<LandspeedException>(() => parser.Feed(bad));
        Assert.Equal(LandspeedErrorKind.FrameLengthZero, ex.Kind);
    }

    [Fact]
    public void Parser_RejectsUnknownType()
    {
        var parser = new FrameStreamParser();
        // len=1, type=0x99 (reserved)
        var bad = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x99 };
        var ex = Assert.Throws<LandspeedException>(() => parser.Feed(bad));
        Assert.Equal(LandspeedErrorKind.UnknownFrameType, ex.Kind);
        Assert.Equal((byte)0x99, ex.Context["type"]);
    }

    [Fact]
    public void Parser_RejectsFrameTooLarge()
    {
        var parser = new FrameStreamParser(maxFrameSize: 1024);
        // len = 2000 > 1024
        var bad = new byte[] { 0x00, 0x00, 0x07, 0xD0, 0x01 };
        var ex = Assert.Throws<LandspeedException>(() => parser.Feed(bad));
        Assert.Equal(LandspeedErrorKind.FrameTooLarge, ex.Kind);
        Assert.Equal(2000u, ex.Context["declaredLength"]);
        Assert.Equal(1024, ex.Context["limit"]);
    }

    [Fact]
    public void WireSize_MatchesEncoded()
    {
        var frame = new Frame(FrameType.DataBinary, new byte[65_536]);
        var wire = FrameCodec.Encode(frame);
        Assert.Equal(frame.WireSize, wire.Length);
    }
}
