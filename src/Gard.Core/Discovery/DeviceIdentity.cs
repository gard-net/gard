using System.Runtime.InteropServices;
using Gard.Core.Protocol;

namespace Gard.Core.Discovery;

/// <summary>Descripción de este dispositivo tal y como se anuncia a otros peers.</summary>
public sealed record DeviceIdentity
{
    public required string Name { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required string AppVersion { get; init; }
    public Capabilities Caps { get; init; } = Capabilities.DefaultV1;

    public static PeerPlatform CurrentPlatform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PeerPlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return PeerPlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return PeerPlatform.Macos;
            return PeerPlatform.Linux;
        }
    }
}
