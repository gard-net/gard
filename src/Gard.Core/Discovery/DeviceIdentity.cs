using Gard.Core.Protocol;

namespace Gard.Core.Discovery;

/// <summary>Descripción de este dispositivo tal y como se anuncia a otros peers.</summary>
public sealed record DeviceIdentity
{
    public required string Name { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required string AppVersion { get; init; }
    public Capabilities Caps { get; init; } = Capabilities.DefaultV1;

    /// <summary>Plataforma actual. En el Core es siempre <c>Windows</c>;
    /// el futuro port Android devolverá <c>Android</c>.</summary>
    public static PeerPlatform CurrentPlatform => PeerPlatform.Windows;
}
