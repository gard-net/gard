using Gard.Core.Protocol;

namespace Gard.Core.Discovery;

/// <summary>
/// Un peer descubierto via mDNS. La resolución a IP:port ocurre en la capa de
/// plataforma (<c>Dnssd</c> en Windows, <c>NsdManager</c> en Android).
/// </summary>
public sealed record DiscoveredPeer
{
    public required string ServiceName { get; init; }
    public required string DeviceName { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required Capabilities Caps { get; init; }
    public required int ProtocolVersionMajor { get; init; }
    /// <summary>Hostname resuelto (o del servicio si aún no ha resuelto).</summary>
    public string? Host { get; init; }
    public int? Port { get; init; }
}
