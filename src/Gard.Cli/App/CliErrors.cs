using System.Net.Sockets;
using Gard.Core.Protocol;

namespace Gard.Cli;

public static class CliErrors
{
    /// <summary>Argumentos inválidos o comando desconocido.</summary>
    public const int UsageExitCode = 2;

    /// <summary>Uso correcto pero la operación falló (red, timeout, protocolo).</summary>
    public const int RuntimeExitCode = 1;

    public static string HintFor(string? command) =>
        command is not null && CliSpec.Find(command) is not null
            ? $"Try 'gard {command} --help'."
            : "Try 'gard --help'.";

    /// <summary>Mensaje accionable para fallos de pairing del lado cliente.</summary>
    public static string PairingMessage(LandspeedException ex) => ex.Kind switch
    {
        LandspeedErrorKind.PairingRequired =>
            "host requires pairing; pass --pair-code <6 digits> (shown in the host's terminal)",
        _ => ex.Context.TryGetValue("reason", out var r) && r is string reason
            ? $"pairing rejected by host: {reason}"
            : "pairing rejected by host: invalid code",
    };

    /// <summary>
    /// Mensaje en inglés y accionable para errores de ejecución. Core produce
    /// mensajes en español para logs internos; la CLI pública habla un solo idioma.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "connection refused; is 'gard host' running on that address and port?",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
            "host not found; check the name or use an IP address",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            "host unreachable; check that both devices are on the same network",
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            "port already in use; pick another with --port or use --dynamic-port",
        SocketException s => $"network error: {s.Message}",
        LandspeedException { Kind: LandspeedErrorKind.Timeout } =>
            "timed out waiting for the peer; check firewalls (TCP 7737 and ephemeral data ports)",
        LandspeedException { Kind: LandspeedErrorKind.IncompatibleProtocolVersion } l =>
            $"incompatible protocol version ({l.Context.GetValueOrDefault("peerVersion")}); update gard on both ends",
        LandspeedException { Kind: LandspeedErrorKind.PayloadOutOfRange } l =>
            $"payload size rejected by host: {l.Context.GetValueOrDefault("peerMessage") ?? l.Message}",
        LandspeedException { Kind: LandspeedErrorKind.PairingRequired or LandspeedErrorKind.PairingInvalid } l =>
            PairingMessage(l),
        LandspeedException { Kind: LandspeedErrorKind.UdpNotNegotiated } =>
            "the peer does not support UDP tests; use --transport tcp",
        LandspeedException { Kind: LandspeedErrorKind.TestRejected } l =>
            $"test rejected by host: {l.Context.GetValueOrDefault("reason")}",
        LandspeedException { Kind: LandspeedErrorKind.Transport } l when l.Context.ContainsKey("closed") =>
            "the peer closed the connection unexpectedly; it may be an older version, or it stopped (try 'gard info <host>')",
        LandspeedException { Kind: LandspeedErrorKind.Transport } l =>
            $"connection to the peer failed: {l.Context.GetValueOrDefault("description") ?? l.Message}",
        _ => ex.Message,
    };
}
