namespace Gard.Cli;

public static class CliErrors
{
    /// <summary>Argumentos inválidos o comando desconocido.</summary>
    public const int UsageExitCode = 2;

    /// <summary>Uso correcto pero la operación falló (red, timeout, protocolo).</summary>
    public const int RuntimeExitCode = 1;

    public static string HintFor(string? command) => command switch
    {
        "scan" or "host" or "test" or "watch" or "info" or "doctor" => $"Try 'gard {command} --help'.",
        _ => "Try 'gard --help'.",
    };
}
