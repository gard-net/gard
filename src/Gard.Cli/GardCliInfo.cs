using Gard.Core.Protocol;

namespace Gard.Cli;

public static class GardCliInfo
{
    public const string VersionNumber = "0.2.6";
    public const string AppVersion = "gard-0.2.6";

    /// <summary>Sesiones simultáneas que acepta <c>gard host</c>; el resto se cierra al aceptar.</summary>
    public const int MaxConcurrentHostSessions = 4;

    public static string VersionString => $"gard {VersionNumber} (LSP/{ProtocolVersion.Current})";
}
