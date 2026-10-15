using System.Text;

namespace Gard.Cli;

/// <summary>
/// Help generado desde <see cref="CliSpec"/>: lo que se documenta es lo que se
/// valida y lo que completan los shells. Texto plano; <see cref="Highlight"/> lo
/// colorea para terminales ricos.
/// </summary>
public static class CliHelp
{
    private const int OptionColumn = 26;

    public static string General()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{GardCliInfo.VersionString}");
        sb.AppendLine("Measure local network quality between devices you own.");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  gard <command> [options]");
        sb.AppendLine("  gard help <command>");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        foreach (var c in CliSpec.Commands)
            sb.AppendLine($"  {c.Name,-13}{c.Summary}");
        sb.AppendLine();
        sb.AppendLine("Global options:");
        AppendOptions(sb, CliSpec.GlobalOptions);
        sb.AppendLine($"  {"-v, --version",-(OptionColumn - 2)}Show version.");
        sb.AppendLine();
        sb.AppendLine("Output formats:");
        sb.AppendLine("  human (default)         Dashboard on a TTY, plain lines otherwise.");
        sb.AppendLine("  --json                  Machine-readable JSON (schema gard.*/1) on stdout.");
        sb.AppendLine("  --csv                   One CSV row per test (gard test).");
        sb.AppendLine();
        sb.AppendLine("Exit codes:");
        sb.AppendLine("  0 ok   1 runtime failure   2 usage error   130 interrupted");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  gard host --pair");
        sb.AppendLine("  gard scan");
        sb.AppendLine("  gard test 192.168.1.50 -t 10 -P 4");
        sb.AppendLine("  gard watch 192.168.1.50 -i 5");
        sb.AppendLine();
        sb.Append("Run 'gard help <command>' for command options.");
        return sb.ToString();
    }

    public static string ForCommand(string command) =>
        CliSpec.Find(command) is { } spec ? Command(spec) : General();

    public static string Command(CliCommandSpec c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"gard {c.Name} {Dash} {c.Summary}");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        foreach (var u in c.Usage) sb.AppendLine($"  {u}");
        if (c.Options.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            AppendOptions(sb, c.Options);
        }
        if (c.Notes is { } notes)
        {
            sb.AppendLine();
            sb.AppendLine(notes);
        }
        sb.AppendLine();
        sb.AppendLine("Global options:");
        AppendOptions(sb, CliSpec.GlobalOptions);
        if (c.Examples.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var e in c.Examples) sb.AppendLine($"  {e}");
        }
        return sb.ToString().TrimEnd();
    }

    // Shims de compatibilidad para llamadores existentes.
    public static string Scan() => ForCommand("scan");
    public static string Host() => ForCommand("host");
    public static string Test() => ForCommand("test");
    public static string Watch() => ForCommand("watch");
    public static string Info() => ForCommand("info");
    public static string Doctor() => ForCommand("doctor");

    /// <summary>Encabezados en negrita y nombres de opciones/comandos en color.</summary>
    public static string Highlight(string help, CliTerminal term)
    {
        if (!term.Color) return help;
        var lines = help.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0 && char.IsUpper(line[0]) && line.EndsWith(':'))
            {
                lines[i] = term.Bold(line);
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ')
            {
                // Primera "palabra" de la fila (opción, comando o ejemplo).
                var body = line[2..];
                var cut = body.IndexOf("  ", StringComparison.Ordinal);
                lines[i] = cut > 0
                    ? "  " + term.Cyan(body[..cut]) + body[cut..]
                    : "  " + term.Cyan(body);
            }
        }
        return string.Join('\n', lines);
    }

    private const string Dash = "-";

    private static void AppendOptions(StringBuilder sb, IEnumerable<CliOptionSpec> options)
    {
        foreach (var o in options)
        {
            var names = (o.Short is null ? "    " : $"{o.Short}, ") + o.Name + (o.Value is null ? "" : $" {o.Value}");
            sb.AppendLine(names.Length >= OptionColumn - 2
                ? $"  {names}{Environment.NewLine}  {new string(' ', OptionColumn - 2)}{o.Help}"
                : $"  {names.PadRight(OptionColumn - 2)}{o.Help}");
        }
    }
}
