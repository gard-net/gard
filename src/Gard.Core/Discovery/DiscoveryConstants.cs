namespace Gard.Core.Discovery;

/// <summary>
/// Constantes de protocolo para descubrimiento vía mDNS/DNS-SD. Deben coincidir
/// con los peers Swift/Android para que los dispositivos se vean entre sí.
/// </summary>
public static class DiscoveryConstants
{
    /// <summary>Tipo de servicio Bonjour/DNS-SD anunciado. Dominio implícito <c>.local</c>.</summary>
    public const string ServiceType = "_landspeed._tcp";

    /// <summary>Puerto TCP por defecto del control plane. Si está ocupado, se usa un puerto dinámico y se anuncia ese.</summary>
    public const int DefaultPort = 7737;

    /// <summary>Major de protocolo anunciado en TXT (<c>v=</c>). Peers con distinto major se ignoran.</summary>
    public const int ProtocolVersionMajor = 1;
}
