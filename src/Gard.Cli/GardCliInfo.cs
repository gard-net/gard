using Gard.Core.Protocol;

namespace Gard.Cli;

public static class GardCliInfo
{
    public const string VersionNumber = "0.2.6";
    public const string AppVersion = "gard-0.2.6";

    public static string VersionString => $"gard {VersionNumber} (LSP/{ProtocolVersion.Current})";
}
