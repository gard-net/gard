using System.Reflection;
using Gard.Core.Protocol;

namespace Gard.Cli;

public static class GardCliInfo
{
    /// <summary>
    /// Versión del CLI, leída del assembly (Directory.Build.props o
    /// <c>-p:Version=</c> en el release). Sin el sufijo <c>+commit</c> de SourceLink.
    /// </summary>
    public static string VersionNumber { get; } = ReadVersion();

    public static string AppVersion => $"gard-{VersionNumber}";

    /// <summary>Sesiones simultáneas que acepta <c>gard host</c>; el resto se cierra al aceptar.</summary>
    public const int MaxConcurrentHostSessions = 4;

    public static string VersionString => $"gard {VersionNumber} (LSP/{ProtocolVersion.Current})";

    private static string ReadVersion()
    {
        var info = typeof(GardCliInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
