using System.Text.Json.Serialization;

namespace Gard.Core.Protocol;

// Todas las propiedades usan snake_case vía JsonNamingPolicy.SnakeCaseLower
// (configurado en ControlMessageCodec). Para renombrados no triviales se usa
// [JsonPropertyName]. Campos opcionales emitidos sólo cuando != null vía
// DefaultIgnoreCondition.WhenWritingNull.

public sealed record HelloBody
{
    public required ProtocolVersion ProtocolVersion { get; init; }
    public required string AppVersion { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required string DeviceName { get; init; }
    public required Capabilities Caps { get; init; }
    /// <summary>Nonce aleatorio de 16 bytes (base64) para prevenir replay.</summary>
    public required string Nonce { get; init; }
}

public sealed record HelloAckBody
{
    public required ProtocolVersion ProtocolVersion { get; init; }
    public required string SessionId { get; init; }
    public required ulong ServerTimeNs { get; init; }
    public required Capabilities Caps { get; init; }
    public required bool RequiresPairing { get; init; }
}

public sealed record PairRequestBody
{
    public required string PairingCode { get; init; }
}

public sealed record PairAckBody
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
}

public sealed record ClockSyncBody
{
    public required ulong T0Ns { get; init; }
}

public sealed record ClockSyncAckBody
{
    public required ulong T0Ns { get; init; }
    public required ulong T1Ns { get; init; }
    public required ulong T2Ns { get; init; }
}

public sealed record PingBody
{
    public required uint Seq { get; init; }
    public required ulong SentNs { get; init; }
}

public sealed record PongBody
{
    public required uint Seq { get; init; }
    public required ulong SentNs { get; init; }
    public required ulong ReceivedNs { get; init; }
}

public sealed record TestStartBody
{
    public required TestDirection Direction { get; init; }
    public required double DurationS { get; init; }
    public required int Streams { get; init; }
    public required int PayloadSize { get; init; }
    public required double WarmupS { get; init; }
    /// <summary>LSP/1.1. Sólo si <c>Direction == Bidir</c>. null ⇒ simultaneous.</summary>
    public BidirMode? BidirMode { get; init; }
    /// <summary>LSP/1.1. Pausa entre up y down en modo secuencial.</summary>
    public double? GapS { get; init; }
}

public sealed record TestStartAckBody
{
    public required bool Accepted { get; init; }
    public required IReadOnlyList<int> DataPorts { get; init; }
    public string? Reason { get; init; }
}

public sealed record TestTickBody
{
    public required double ElapsedS { get; init; }
    public required ulong BytesUp { get; init; }
    public required ulong BytesDown { get; init; }
    public required double PingAvgMs { get; init; }
    public required double JitterMs { get; init; }
    public required double LossPct { get; init; }
}

public sealed record TestEndBody
{
    // Payload vacío: se serializa como "{}".
}

public sealed record ThroughputBody
{
    public required ulong MeanBps { get; init; }
    public required ulong PeakBps { get; init; }
    public required IReadOnlyList<ulong> PerStreamBps { get; init; }
}

public sealed record LatencyBody
{
    public required double Min { get; init; }
    public required double Avg { get; init; }
    public required double Max { get; init; }
    public required double P95 { get; init; }
}

public sealed record ResultBody
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required TestDirection Direction { get; init; }
    public required int Streams { get; init; }
    public required double DurationS { get; init; }
    public required ThroughputBody Throughput { get; init; }
    public required LatencyBody LatencyMs { get; init; }
    public required double JitterMs { get; init; }
    public required double LossPct { get; init; }
    public required int Samples { get; init; }

    /// <summary>LSP/1.1. RTT bajo carga via DATA_BINARY_ECHO.</summary>
    public RttUnderLoadStats? RttUnderLoad { get; init; }
    /// <summary>LSP/1.1. Reporte granular de intervalos.</summary>
    public IntervalReport? Intervals { get; init; }
    /// <summary>LSP/1.1. Throughput fase up en bidir secuencial.</summary>
    public ThroughputBody? ThroughputUp { get; init; }
    /// <summary>LSP/1.1. Throughput fase down en bidir secuencial.</summary>
    public ThroughputBody? ThroughputDown { get; init; }
    public required ProtocolVersion ProtocolVersion { get; init; }
}

public sealed record GoodbyeBody
{
    public string? Reason { get; init; }
}

public sealed record ErrorBody
{
    public required int Code { get; init; }
    public required string Message { get; init; }
}

// ─── LSP/1.1: estadísticas incluibles en ResultBody ──────────────────────────

public sealed record RttUnderLoadStats
{
    public int Samples { get; init; }
    public double MinMs { get; init; }
    public double MedianMs { get; init; }
    public double P95Ms { get; init; }
    public double MaxMs { get; init; }
    public double StdevMs { get; init; }
    public double BaselineMedianMs { get; init; }
    public int SpikesCount { get; init; }

    public static readonly RttUnderLoadStats Empty = new();
}

public sealed record IntervalSample
{
    public required double StartS { get; init; }
    public required double EndS { get; init; }
    public required ulong Bps { get; init; }
}

public sealed record IntervalStats
{
    public ulong MedianBps { get; init; }
    public ulong P95Bps { get; init; }
    public ulong StdevBps { get; init; }
    public ulong MinBps { get; init; }
    public ulong MaxBps { get; init; }
}

public sealed record IntervalReport
{
    public required int WindowMs { get; init; }
    public required IReadOnlyList<IntervalSample> Samples { get; init; }
    public required IntervalStats Stats { get; init; }
}
