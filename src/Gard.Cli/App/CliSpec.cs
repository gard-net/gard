namespace Gard.Cli;

/// <param name="Name">Forma larga canónica (<c>--duration</c>).</param>
/// <param name="Value">Nombre del valor (<c>S</c>) o <c>null</c> si es un flag.</param>
/// <param name="Short">Forma corta (<c>-t</c>), estilo iperf3 cuando aplica.</param>
/// <param name="Expand">Atajo que se reescribe a otras opciones (<c>--udp</c> ⇒ <c>--transport udp</c>).</param>
/// <param name="DeprecatedAliases">Nombres viejos aceptados con aviso.</param>
public sealed record CliOptionSpec(
    string Name,
    string? Value,
    string Help,
    string? Short = null,
    string[]? Expand = null,
    string[]? DeprecatedAliases = null)
{
    public bool TakesValue => Value is not null;
}

public sealed record CliCommandSpec(
    string Name,
    string Summary,
    string[] Usage,
    CliOptionSpec[] Options,
    string[] Examples,
    int MaxPositionals = 0,
    string? Notes = null);

/// <summary>
/// Fuente única de comandos y opciones. De acá salen el help, la validación
/// (opciones desconocidas ⇒ exit 2 con sugerencia, en vez de ignorarlas) y los
/// scripts de completion.
/// </summary>
public static class CliSpec
{
    public static readonly CliOptionSpec[] GlobalOptions =
    [
        new("--plain", null, "Plain line output, no layout or color."),
        new("--no-color", null, "Keep the layout, disable ANSI color (also NO_COLOR=1)."),
        new("--ascii", null, "ASCII-only glyphs for terminals without Unicode."),
        new("--style", "STYLE", "rich or plain. Default: rich."),
        new("--help", null, "Show help.", Short: "-h"),
    ];

    private static readonly CliOptionSpec Port = new("--port", "N", "TCP control port. Default: 7737.", Short: "-p");
    private static readonly CliOptionSpec HostOpt = new("--host", "HOST", "Peer IP address or DNS name (or pass it positionally).");
    private static readonly CliOptionSpec PairCode = new("--pair-code", "CODE",
        "6-digit code for hosts started with --pair. Prompted on a TTY if omitted.");

    private static readonly CliOptionSpec[] MeasurementOptions =
    [
        HostOpt,
        Port,
        new("--direction", "DIR", "up, down or bidir. Default: down.", DeprecatedAliases: ["--dir"]),
        new("--streams", "N", "Parallel streams, 1..32. Default: 1.", Short: "-P"),
        new("--duration", "S", "Measurement seconds, max 3600. Default: 10.", Short: "-t"),
        new("--warmup", "S", "Seconds excluded at the start, max 60. Default: 1.", Short: "-O"),
        new("--payload", "BYTES", "Payload size. TCP 512..4194304 (default 65536), UDP 64..65507 (default 1200).", Short: "-l"),
        new("--transport", "T", "tcp or udp. Default: tcp."),
        new("--udp", null, "Shortcut for --transport udp.", Short: "-u", Expand: ["--transport", "udp"]),
        new("--bitrate", "BPS", "UDP target per stream, e.g. 200M or 1.5G. 0 = unlimited.", Short: "-b"),
        new("--bidir-mode", "M", "simultaneous or sequential (TCP only). UDP bidir needs 2+ streams."),
        new("--gap", "S", "Pause between sequential bidir phases, max 60. Default: 0.5."),
        PairCode,
    ];

    public static readonly CliCommandSpec[] Commands =
    [
        new("scan", "Discover LSP peers on the local network.",
            ["gard scan [--seconds N] [--all] [--json]"],
            [
                new("--seconds", "N", "Scan duration in seconds. Default: 5.", Short: "-t"),
                new("--all", null, "Also list cached announcements that do not answer.", Short: "-a"),
                new("--json", null, "Print peers as JSON (includes reachability).", Short: "-J"),
            ],
            ["gard scan", "gard scan -t 3 --json"]),

        new("host", "Listen for tests and advertise over mDNS.",
            ["gard host [--name NAME] [--port N] [--pair | --pair-code CODE]"],
            [
                new("--name", "NAME", "Advertised device name. Default: host name.", Short: "-n"),
                Port,
                new("--dynamic-port", null, "Let the OS pick a free port."),
                new("--pair", null, "Require a 6-digit pairing code, shown at start."),
                new("--pair-code", "CODE", "Require this 6-digit pairing code."),
            ],
            ["gard host", "gard host --name lab-mac --pair", "gard host --dynamic-port"]),

        new("test", "Measure throughput, latency, jitter and loss against a peer.",
            ["gard test <host> [options]", "gard test --csv-header"],
            [
                .. MeasurementOptions,
                new("--format", "F", "human, json or csv. Default: human."),
                new("--json", null, "Shortcut for --format json.", Short: "-J", Expand: ["--format", "json"]),
                new("--csv", null, "Shortcut for --format csv.", Expand: ["--format", "csv"]),
                new("--label", "ID", "Label included in CSV and JSON output."),
                new("--csv-header", null, "Print the CSV header and exit."),
                new("--quiet", null, "No progress on stderr.", Short: "-q"),
            ],
            [
                "gard test 192.168.1.50",
                "gard test 192.168.1.50 -t 10 -P 4 --direction bidir",
                "gard test 192.168.1.50 -u -b 200M -l 1200 --direction up",
                "gard test 192.168.1.50 --json | jq .throughput.mean_bps",
            ],
            MaxPositionals: 1),

        new("watch", "Run repeated short measurements until Ctrl+C.",
            ["gard watch <host> [options]"],
            [
                .. MeasurementOptions,
                new("--interval", "S", "Seconds between samples. Default: 5.", Short: "-i"),
                new("--count", "N", "Stop after N samples. Default: run until Ctrl+C.", Short: "-n"),
                new("--json", null, "One JSON object per sample (NDJSON).", Short: "-J"),
            ],
            ["gard watch 192.168.1.50", "gard watch 192.168.1.50 -i 10 -n 6 --json"],
            MaxPositionals: 1,
            Notes: "Per-sample defaults: --duration 3 --warmup 0."),

        new("info", "Show a peer's protocol version and capabilities.",
            ["gard info <host> [--port N] [--json]"],
            [HostOpt, Port, PairCode, new("--json", null, "Print as JSON.", Short: "-J")],
            ["gard info 192.168.1.50"],
            MaxPositionals: 1),

        new("doctor", "Diagnose the local environment and terminal.",
            ["gard doctor [--json]"],
            [new("--json", null, "Print as JSON.", Short: "-J")],
            ["gard doctor"]),

        new("completions", "Print a shell completion script.",
            ["gard completions <bash|zsh|fish|powershell>"],
            [],
            [
                "gard completions bash > ~/.local/share/bash-completion/completions/gard",
                "gard completions zsh > \"${fpath[1]}/_gard\"",
                "gard completions fish > ~/.config/fish/completions/gard.fish",
            ],
            MaxPositionals: 1),

        new("version", "Show the gard and protocol versions.",
            ["gard version"], [], ["gard version"]),
    ];

    public static CliCommandSpec? Find(string name) =>
        Commands.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// Normaliza y valida los argumentos de un comando: separa <c>--x=v</c>,
    /// traduce formas cortas y alias a la forma larga, expande atajos y rechaza
    /// opciones desconocidas. Devuelve los args listos para <see cref="CliOptions"/>
    /// y los avisos de deprecación a mostrar.
    /// </summary>
    public static (string[] Args, IReadOnlyList<string> Warnings) Normalize(CliCommandSpec command, string[] args)
    {
        var options = command.Options;
        var output = new List<string>();
        var warnings = new List<string>();
        var positionals = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            string? inlineValue = null;
            if (token.StartsWith("--", StringComparison.Ordinal) && token.IndexOf('=') is > 2 and var eq)
            {
                inlineValue = token[(eq + 1)..];
                token = token[..eq];
            }

            if (!IsOptionToken(token))
            {
                positionals++;
                if (positionals > command.MaxPositionals)
                    throw new CliOptionException($"unexpected argument '{token}' for 'gard {command.Name}'");
                output.Add(token);
                continue;
            }

            if (token is "-h" or "--help") { output.Add("--help"); continue; }

            var spec = options.FirstOrDefault(o => o.Name == token || o.Short == token);
            if (spec is null && options.FirstOrDefault(o => o.DeprecatedAliases?.Contains(token) == true) is { } renamed)
            {
                warnings.Add($"'{token}' is deprecated; use '{renamed.Name}'");
                spec = renamed;
            }
            if (spec is null)
            {
                var hint = Suggest(token, options.Select(o => o.Name).Concat(GlobalOptions.Select(o => o.Name)));
                throw new CliOptionException(
                    $"unknown option '{token}' for 'gard {command.Name}'" + (hint is null ? "" : $"; did you mean '{hint}'?"));
            }

            if (spec.Expand is { } expansion)
            {
                if (inlineValue is not null) throw new CliOptionException($"{spec.Name} does not take a value");
                output.AddRange(expansion);
                continue;
            }

            output.Add(spec.Name);
            if (!spec.TakesValue)
            {
                if (inlineValue is not null) throw new CliOptionException($"{spec.Name} does not take a value");
                continue;
            }
            if (inlineValue is not null) { output.Add(inlineValue); continue; }
            if (i == args.Length - 1 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliOptionException($"{spec.Name} requires a value");
            output.Add(args[++i]);
        }
        return (output.ToArray(), warnings);
    }

    /// <summary>Sugerencia por distancia de edición (≤ 2) para un nombre mal escrito.</summary>
    public static string? Suggest(string input, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Distance(input, c);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return bestDistance <= 2 ? best : null;
    }

    // "-5" o "-1M" son valores (p. ej. de --bitrate), no opciones.
    private static bool IsOptionToken(string token) =>
        token.Length > 1 && token[0] == '-' && !char.IsAsciiDigit(token[1]) && token[1] != '.';

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
