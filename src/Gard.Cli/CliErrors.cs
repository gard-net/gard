namespace Gard.Cli;

public static class CliErrors
{
    public static string HintFor(string? command) => command switch
    {
        "scan" => "Try 'gard scan --help'.",
        "host" => "Try 'gard host --help'.",
        "test" => "Try 'gard test --help'.",
        _ => "Try 'gard --help'.",
    };
}
