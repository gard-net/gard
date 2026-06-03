namespace Gard.Cli;

public sealed record CliStyleOptions(bool Plain, bool NoColor, string Style)
{
    public static CliStyleOptions Default { get; } = new(false, false, "rich");
}

public sealed class CliTerminal
{
    public CliTerminal(CliStyleOptions options, bool? forceInteractive = null)
    {
        Plain = options.Plain || options.Style == "plain";
        var interactive = forceInteractive ?? !Console.IsOutputRedirected;
        var envNoColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var honorEnvironment = forceInteractive is null;
        var dumb = honorEnvironment && string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
        var ci = honorEnvironment && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
        Rich = !Plain && options.Style == "rich" && interactive && !dumb && !ci;
        Color = Rich && !options.NoColor && !envNoColor;
    }

    public bool Plain { get; }
    public bool Rich { get; }
    public bool Color { get; }

    public string Paint(string text, string ansi) => Color ? $"{ansi}{text}{CliTheme.Reset}" : text;
    public string Cyan(string text) => Paint(text, CliTheme.Cyan);
    public string Green(string text) => Paint(text, CliTheme.Green);
    public string Yellow(string text) => Paint(text, CliTheme.Yellow);
    public string Red(string text) => Paint(text, CliTheme.Red);
    public string Purple(string text) => Paint(text, CliTheme.Purple);
    public string Dim(string text) => Paint(text, CliTheme.Dim);
    public string Bold(string text) => Paint(text, CliTheme.Bold);
}

public static class CliTheme
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";
    public const string Cyan = "\u001b[36m";
    public const string Green = "\u001b[32m";
    public const string Yellow = "\u001b[33m";
    public const string Red = "\u001b[31m";
    public const string Purple = "\u001b[35m";
}
