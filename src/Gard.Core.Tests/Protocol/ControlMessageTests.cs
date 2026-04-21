using System.Text.Json;
using System.Text.Json.Nodes;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Protocol;

public class ControlMessageTests
{
    /// <summary>
    /// Round-trip a nivel wire: encode → decode → encode debe dar bytes idénticos.
    /// Usado cuando el body contiene colecciones (IReadOnlyList) y la igualdad
    /// estructural de records no aplica por el tipo concreto distinto al decodificar.
    /// </summary>
    private static void AssertWireRoundTrip(ControlMessage msg)
    {
        var wire = ControlMessageCodec.Encode(msg);
        var decoded = ControlMessageCodec.Decode(wire);
        Assert.Equal(msg.T, decoded.T);
        Assert.Equal(msg.Id, decoded.Id);
        var reencoded = ControlMessageCodec.Encode(decoded);
        Assert.Equal(wire, reencoded);
    }

    // ── Round-trip de cada variante ──────────────────────────────────────────

    [Fact]
    public void HelloRoundTrip()
    {
        var body = new HelloBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            AppVersion = "1.0.0",
            Platform = PeerPlatform.Ios,
            DeviceName = "iPhone de prueba",
            Caps = Capabilities.DefaultV1,
            Nonce = "YWJjZGVmZ2hpamtsbW5vcA==",
        };
        var msg = new HelloMessage(42, body);
        var wire = ControlMessageCodec.Encode(msg);
        Assert.Equal(msg, ControlMessageCodec.Decode(wire));
    }

    [Fact]
    public void HelloAckRoundTrip()
    {
        var body = new HelloAckBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            SessionId = "00000000-0000-0000-0000-000000000001",
            ServerTimeNs = 1_700_000_000_000_000_000UL,
            Caps = Capabilities.ParallelStreams | Capabilities.Pairing,
            RequiresPairing = true,
        };
        var msg = new HelloAckMessage(42, body);
        var wire = ControlMessageCodec.Encode(msg);
        Assert.Equal(msg, ControlMessageCodec.Decode(wire));
    }

    [Fact]
    public void PairingRoundTrip()
    {
        var req = new PairRequestMessage(1, new PairRequestBody { PairingCode = "482917" });
        var ack = new PairAckMessage(1, new PairAckBody { Ok = true });
        Assert.Equal(req, ControlMessageCodec.Decode(ControlMessageCodec.Encode(req)));
        Assert.Equal(ack, ControlMessageCodec.Decode(ControlMessageCodec.Encode(ack)));
    }

    [Fact]
    public void PingPongRoundTrip()
    {
        var p = new PingMessage(99, new PingBody { Seq = 7, SentNs = 123_456_789UL });
        var pong = new PongMessage(99, new PongBody { Seq = 7, SentNs = 123_456_789UL, ReceivedNs = 123_456_999UL });
        Assert.Equal(p, ControlMessageCodec.Decode(ControlMessageCodec.Encode(p)));
        Assert.Equal(pong, ControlMessageCodec.Decode(ControlMessageCodec.Encode(pong)));
    }

    [Fact]
    public void TestFlowRoundTrip()
    {
        var start = new TestStartMessage(10, new TestStartBody
        {
            Direction = TestDirection.Down, DurationS = 10.0, Streams = 4,
            PayloadSize = 65_536, WarmupS = 1.0,
        });
        var ack = new TestStartAckMessage(10, new TestStartAckBody
        {
            Accepted = true, DataPorts = new[] { 7738, 7739, 7740, 7741 },
        });
        var tick = new TestTickMessage(11, new TestTickBody
        {
            ElapsedS = 1.2, BytesUp = 0UL, BytesDown = 120_000_000UL,
            PingAvgMs = 0.42, JitterMs = 0.11, LossPct = 0.0,
        });
        var end = new TestEndMessage(12, new TestEndBody());

        foreach (ControlMessage msg in new ControlMessage[] { start, ack, tick, end })
        {
            AssertWireRoundTrip(msg);
        }
    }

    [Fact]
    public void ResultRoundTrip()
    {
        var body = new ResultBody
        {
            SessionId = "UUID-1",
            StartedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            EndedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010),
            Direction = TestDirection.Down,
            Streams = 4,
            DurationS = 10.0,
            Throughput = new ThroughputBody
            {
                MeanBps = 942_873_421UL,
                PeakBps = 981_223_910UL,
                PerStreamBps = new ulong[] { 236_000_000, 235_000_000, 235_900_000, 235_973_421 },
            },
            LatencyMs = new LatencyBody { Min = 0.42, Avg = 0.71, Max = 3.11, P95 = 1.12 },
            JitterMs = 0.23,
            LossPct = 0.0,
            Samples = 234,
            ProtocolVersion = ProtocolVersion.Current,
        };
        var msg = new ResultMessage(42, body);
        AssertWireRoundTrip(msg);
    }

    [Fact]
    public void ErrorRoundTrip()
    {
        var msg = new ErrorMessage(5, new ErrorBody { Code = 1001, Message = "frame too large" });
        Assert.Equal(msg, ControlMessageCodec.Decode(ControlMessageCodec.Encode(msg)));
    }

    // ── Discriminador y campos inyectados ────────────────────────────────────

    [Fact]
    public void EncodedJsonContainsTAndId()
    {
        var msg = new PingMessage(123, new PingBody { Seq = 1, SentNs = 456UL });
        var wire = ControlMessageCodec.Encode(msg);
        var root = (JsonNode.Parse(wire) as JsonObject)!;
        Assert.Equal("ping", (string?)root["t"]);
        Assert.Equal(123UL, (ulong?)root["id"]);
        Assert.Equal(1u, (uint?)root["seq"]);
        Assert.Equal(456UL, (ulong?)root["sent_ns"]);
    }

    [Fact]
    public void EncodedJson_KeysAreSorted()
    {
        var msg = new PingMessage(1, new PingBody { Seq = 7, SentNs = 42UL });
        var wire = ControlMessageCodec.Encode(msg);
        var json = System.Text.Encoding.UTF8.GetString(wire);
        // Orden alfabético ordinal: "id" < "sent_ns" < "seq" < "t"
        // ("sent_ns" precede a "seq" porque 'n' (0x6E) < 'q' (0x71)).
        Assert.Equal("{\"id\":1,\"sent_ns\":42,\"seq\":7,\"t\":\"ping\"}", json);
    }

    // ── Errores ──────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownControlTypeThrows()
    {
        var json = "{\"t\":\"no_existe\",\"id\":1}"u8.ToArray();
        var ex = Assert.Throws<LandspeedException>(() => ControlMessageCodec.Decode(json));
        Assert.Equal(LandspeedErrorKind.UnknownControlMessageType, ex.Kind);
        Assert.Equal("no_existe", ex.Context["t"]);
    }

    [Fact]
    public void MalformedJsonThrows()
    {
        var json = "no soy json"u8.ToArray();
        var ex = Assert.Throws<LandspeedException>(() => ControlMessageCodec.Decode(json));
        Assert.Equal(LandspeedErrorKind.MalformedControlJson, ex.Kind);
    }

    [Fact]
    public void MissingFieldsThrowsMalformed()
    {
        // "hello" sin los campos requeridos.
        var json = "{\"t\":\"hello\",\"id\":1}"u8.ToArray();
        var ex = Assert.Throws<LandspeedException>(() => ControlMessageCodec.Decode(json));
        Assert.Equal(LandspeedErrorKind.MalformedControlJson, ex.Kind);
    }

    // ── Frame.control round-trip ─────────────────────────────────────────────

    [Fact]
    public void ControlMessageThroughFrameLayer()
    {
        var original = new GoodbyeMessage(7, new GoodbyeBody { Reason = "ok" });
        var frame = ControlMessageCodec.ToFrame(original);
        var wire = FrameCodec.Encode(frame);

        var parser = new FrameStreamParser();
        var frames = parser.Feed(wire);
        Assert.Single(frames);
        Assert.Equal(FrameType.ControlJson, frames[0].Type);

        var decoded = ControlMessageCodec.Decode(frames[0].Payload.Span);
        Assert.Equal(original, decoded);
    }
}
