using Gard.Core.Protocol;

namespace Gard.Core.Handshake;

/// <summary>Resultado negociado de un handshake LSP/1.</summary>
public sealed record HandshakeOutcome
{
    public required string SessionId { get; init; }
    /// <summary>Capacidades efectivas (intersección de cliente y host).</summary>
    public required Capabilities NegotiatedCaps { get; init; }
    /// <summary>Versión del peer (misma mayor, puede diferir menor).</summary>
    public required ProtocolVersion PeerVersion { get; init; }
    /// <summary>Plataforma anunciada por el peer durante hello/hello_ack.</summary>
    public required PeerPlatform PeerPlatform { get; init; }
    /// <summary>Offset estimado: <c>peer_ns ≈ local_ns + ClockOffsetNs</c>.</summary>
    public required long ClockOffsetNs { get; init; }
    /// <summary>RTT estimado durante clock_sync (ns).</summary>
    public required ulong ClockSyncRttNs { get; init; }
}
