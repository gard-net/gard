namespace Gard.Cli;

public static class CliErrors
{
    /// <summary>Argumentos inválidos o comando desconocido.</summary>
    public const int UsageExitCode = 2;

    /// <summary>Uso correcto pero la operación falló (red, timeout, protocolo).</summary>
    public const int RuntimeExitCode = 1;

    /// <summary>Mensaje accionable para fallos de pairing del lado cliente.</summary>
    public static string PairingMessage(Gard.Core.Protocol.LandspeedException ex) => ex.Kind switch
    {
        Gard.Core.Protocol.LandspeedErrorKind.PairingRequired =>
            "host requires pairing; pass --pair-code <6 digits> (shown in the host's terminal)",
        _ => ex.Context.TryGetValue("reason", out var r) && r is string reason
            ? $"pairing rejected by host: {reason}"
            : "pairing rejected by host: invalid code",
    };

    public static string HintFor(string? command) => command switch
    {
        "scan" or "host" or "test" or "watch" or "info" or "doctor" => $"Try 'gard {command} --help'.",
        _ => "Try 'gard --help'.",
    };
}
