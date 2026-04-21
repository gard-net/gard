using Gard.Core.Protocol;

namespace Gard.Core.Models;

/// <summary>Resultado final de un test (forma canónica del dominio).</summary>
public sealed record TestResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string SessionId { get; init; }
    public required string PeerName { get; init; }
    public required PeerPlatform PeerPlatform { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required TestDirection Direction { get; init; }
    public required int Streams { get; init; }
    public required double DurationS { get; init; }

    public required ulong MeanBps { get; init; }
    public required ulong PeakBps { get; init; }
    public required IReadOnlyList<ulong> PerStreamBps { get; init; }

    public required double PingMinMs { get; init; }
    public required double PingAvgMs { get; init; }
    public required double PingMaxMs { get; init; }
    public required double PingP95Ms { get; init; }
    public required double JitterMs { get; init; }
    public required double LossPct { get; init; }
    public required int PingSamples { get; init; }

    /// <summary>LSP/1.1: RTT bajo carga. Opcional (requiere <c>dataEcho</c>).</summary>
    public RttUnderLoadStats? RttUnderLoadMs { get; init; }
    /// <summary>LSP/1.1: reporte de intervalos. Opcional (requiere <c>intervalReporting</c>).</summary>
    public IntervalReport? Intervals { get; init; }
    /// <summary>LSP/1.1: throughput fase up en bidir secuencial. Opcional.</summary>
    public ThroughputBody? ThroughputUp { get; init; }
    /// <summary>LSP/1.1: throughput fase down en bidir secuencial. Opcional.</summary>
    public ThroughputBody? ThroughputDown { get; init; }

    /// <summary>LSP/1.2: métricas del data-plane UDP. Null si el test fue TCP.</summary>
    public UdpStatsBody? Udp { get; init; }

    public NetworkInfoMetadata? NetworkInfo { get; init; }
    public ProtocolVersion ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;

    /// <summary>Vuelca el resultado al cuerpo LSP/1 <c>result</c>.</summary>
    public ResultBody AsWireBody() => new()
    {
        SessionId = SessionId,
        StartedAt = StartedAt,
        EndedAt = EndedAt,
        Direction = Direction,
        Streams = Streams,
        DurationS = DurationS,
        Throughput = new ThroughputBody
        {
            MeanBps = MeanBps,
            PeakBps = PeakBps,
            PerStreamBps = PerStreamBps,
        },
        LatencyMs = new LatencyBody
        {
            Min = PingMinMs,
            Avg = PingAvgMs,
            Max = PingMaxMs,
            P95 = PingP95Ms,
        },
        JitterMs = JitterMs,
        LossPct = LossPct,
        Samples = PingSamples,
        RttUnderLoad = RttUnderLoadMs,
        Intervals = Intervals,
        ThroughputUp = ThroughputUp,
        ThroughputDown = ThroughputDown,
        Udp = Udp,
        ProtocolVersion = ProtocolVersion,
    };
}
