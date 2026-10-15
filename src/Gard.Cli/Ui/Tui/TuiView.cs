using System.Globalization;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// Render puro de la TUI: dado un estado y un tamaño devuelve exactamente
/// <c>height</c> líneas de <c>width</c> columnas visibles. Sin I/O: lo testean los
/// unit tests y lo dibuja <see cref="TuiConsole"/>.
///
/// <code>
///  gard 0.4.0 · LSP/1.2                         ● watch 3s   ? help  q quit
/// ╭─ Peers (3) ──────────╮╭─ Test ─────────────────────────────────────────╮
/// │ ▸ Mac        2.1 ms  ││ Mac  192.168.8.102:7737 · macos                │
/// │   iPhone     4.0 ms  ││ down · tcp · 4 streams · 10s                   │
/// │                      ││ ⠹ measuring  ██████░░░░  4.2/10s  912 Mb/s     │
/// ╰──────────────────────╯╰────────────────────────────────────────────────╯
/// ╭─ History ──────────────────────────────────────────────────────────────╮
/// │ TIME      PEER     MODE        DOWN        UP     P95    LOSS  QUALITY │
/// ╰────────────────────────────────────────────────────────────────────────╯
///  enter run · esc cancel · d dir · u tcp/udp · s streams · t time · …
/// </code>
/// </summary>
public static class TuiView
{
    public const int MinWidth = 64;
    public const int MinHeight = 16;

    private static readonly CliTableColumn[] HistoryColumns =
    [
        new("TIME", 8),
        new("PEER", 12),
        new("MODE", 13),
        new("DOWN", 11, Right: true),
        new("UP", 11, Right: true),
        new("P95", 9, Right: true),
        new("LOSS", 7, Right: true),
        new("QUALITY", 9),
    ];

    public static string[] Render(TuiState s, CliTerminal term, int width, int height)
    {
        if (width < MinWidth || height < MinHeight) return TooSmall(term, width, height);

        var lines = new List<string>(height) { Header(s, term, width) };

        var middle = height - 2;
        var historyHeight = Math.Clamp(middle / 3, 5, 12);
        var topHeight = middle - historyHeight;
        var leftWidth = Math.Clamp(width * 34 / 100, 26, 40);
        var rightWidth = width - leftWidth;

        var left = Box(term, $"Peers ({s.Peers.Count})", leftWidth, topHeight, PeerLines(s, term, leftWidth - 4, topHeight - 2), focused: true);
        var right = Box(term, s.ShowHelp ? "Help" : "Test", rightWidth, topHeight,
            s.ShowHelp ? HelpLines(term) : TestLines(s, term, rightWidth - 4, topHeight - 2), focused: false);
        for (var i = 0; i < topHeight; i++) lines.Add(left[i] + right[i]);

        lines.AddRange(Box(term, "History", width, historyHeight, HistoryLines(s, term, width - 4), focused: false));
        lines.Add(Footer(s, term, width));
        return lines.ToArray();
    }

    private static string Header(TuiState s, CliTerminal term, int width)
    {
        var sym = term.Symbols;
        var left = $" {term.Bold("gard " + GardCliInfo.VersionNumber)} {term.Dim($"{sym.Dot} LSP/{ProtocolVersion.Current}")}";
        var parts = new List<string>();
        if (s.Watch)
        {
            var next = s.NextWatchRunAt is { } at && s.Running is null
                ? $" {Math.Max(0, (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds))}s"
                : "";
            parts.Add(term.Green($"{sym.Ok} watch{next}"));
        }
        parts.Add(term.Dim("? help  q quit "));
        var right = string.Join("   ", parts);
        return Pad(left, width - CliText.VisibleLength(right), term) + right;
    }

    private static string Footer(TuiState s, CliTerminal term, int width)
    {
        if (s.Input != TuiInputKind.None)
        {
            var cursor = term.Unicode ? "█" : "_";
            return Fit($" {term.Cyan(s.InputPrompt + ":")} {s.InputText}{cursor}  {term.Dim("enter ok · esc cancel")}", width, term);
        }
        var sym = term.Symbols;
        var hints = s.Running is not null
            ? $"esc cancel {sym.Dot} w watch {sym.Dot} ? help"
            : $"enter run {sym.Dot} d dir {sym.Dot} u tcp/udp {sym.Dot} s streams {sym.Dot} t time {sym.Dot} b rate {sym.Dot} w watch {sym.Dot} a add host {sym.Dot} r rescan";
        return Fit(" " + term.Dim(hints), width, term);
    }

    private static List<string> PeerLines(TuiState s, CliTerminal term, int inner, int rows)
    {
        var sym = term.Symbols;
        var lines = new List<string>();
        if (s.Peers.Count == 0)
        {
            lines.Add(term.Dim($"searching via mDNS{sym.Ellipsis}"));
            lines.Add("");
            lines.Add(term.Dim("press 'a' to add a host"));
            return lines;
        }

        // Ventana que mantiene visible la selección.
        var selected = Math.Max(0, s.SelectedIndex);
        var first = Math.Clamp(selected - rows + 1, 0, Math.Max(0, s.Peers.Count - rows));
        foreach (var p in s.Peers.Skip(first).Take(rows))
        {
            var isSel = p.Key == s.Selected?.Key;
            var latency = p.ConnectMs is { } ms ? $"{ms.ToString("F1", CultureInfo.InvariantCulture)} ms"
                : p.Probed ? "no answer" : sym.Ellipsis;
            var latencyText = p.ConnectMs is not null ? latency : term.Dim(latency);
            var nameWidth = Math.Max(4, inner - 2 - 10);
            var name = CliText.Fit(p.Name + (p.Manual ? "*" : ""), nameWidth, ellipsis: sym.Ellipsis);
            var marker = isSel ? term.Cyan(sym.Pointer + " ") : new string(' ', CliText.VisibleLength(sym.Pointer) + 1);
            var nameText = isSel ? term.Bold(name) : p.ConnectMs is null && p.Probed ? term.Dim(name) : name;
            lines.Add(marker + nameText + CliText.Fit(latencyText, inner - CliText.VisibleLength(marker) - nameWidth, right: true));
        }
        return lines;
    }

    private static List<string> TestLines(TuiState s, CliTerminal term, int inner, int rows)
    {
        var sym = term.Symbols;
        var lines = new List<string>();
        if (s.Selected is { } peer)
        {
            var platform = peer.Platform == PeerPlatform.Unknown ? "" : $" {sym.Dot} {peer.Platform.ToString().ToLowerInvariant()}";
            var udp = peer.Caps == Capabilities.None ? ""
                : peer.Caps.HasFlag(Capabilities.UdpDataPlane) ? $" {sym.Dot} tcp+udp" : $" {sym.Dot} tcp only";
            lines.Add($"{term.Bold(peer.Name)}  {term.Dim($"{peer.Key}{platform}{udp}")}");
        }
        else
        {
            lines.Add(term.Dim("no peer selected"));
        }
        lines.Add(term.Cyan(s.ModeLabel(sym.Dot)));
        lines.Add("");

        if (s.Running is { } run)
        {
            var spinner = term.Cyan(sym.Spinner[s.SpinnerFrame % sym.Spinner.Length]);
            lines.Add($"{spinner} {run.Phase}");
            if (run.Phase == "measuring" && run.TotalS > 0)
            {
                var ratio = Math.Clamp(run.ElapsedS / run.TotalS, 0, 1);
                lines.Add($"{term.Cyan(CliTable.Gauge(ratio, Math.Min(30, inner - 14), sym))}  " +
                          term.Dim($"{run.ElapsedS.ToString("F1", CultureInfo.InvariantCulture)}/{run.TotalS:0}s"));
                lines.Add($"{term.Dim("now ")}{term.Bold(CliHumanFormatter.FormatBps(run.CurrentBps))}");
                if (run.History.Count > 1) lines.Add(term.Purple(CliTable.Sparkline(run.History, inner, sym)));
            }
        }
        else if (s.LastResult is { } r)
        {
            lines.AddRange(ResultLines(r, s.LastResultPeer, term, inner));
        }
        else
        {
            lines.Add(term.Dim("press enter to measure the selected peer"));
        }

        // Estado al pie del panel.
        if (s.Status is { } status)
        {
            while (lines.Count < rows - 1) lines.Add("");
            if (lines.Count >= rows) lines.RemoveRange(rows - 1, lines.Count - (rows - 1));
            lines.Add(s.StatusIsError ? term.Red(status) : term.Dim(status));
        }
        return lines;
    }

    internal static List<string> ResultLines(TestResult r, string? peer, CliTerminal term, int inner)
    {
        var sym = term.Symbols;
        string Row(string label, string value) => term.Dim(label.PadRight(12)) + value;
        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        var lines = new List<string>
        {
            term.Dim($"last result{(peer is null ? "" : $" {sym.Dot} {peer}")} {sym.Dot} {r.EndedAt.ToLocalTime():HH:mm:ss}"),
            Row("Throughput", $"{term.Bold(CliHumanFormatter.FormatBps(r.MeanBps))} {term.Dim("mean")}  {CliHumanFormatter.FormatBps(r.PeakBps)} {term.Dim("peak")}"),
        };
        var series = r.Intervals?.Samples.Select(x => (double)x.Bps).ToArray();
        if (series is { Length: > 1 }) lines.Add(Row("", term.Purple(CliTable.Sparkline(series, inner - 12, sym))));
        if (r.ThroughputDown is { } down) lines.Add(Row($" {sym.Down} down", CliHumanFormatter.FormatBps(down.MeanBps)));
        if (r.ThroughputUp is { } up) lines.Add(Row($" {sym.Up} up", CliHumanFormatter.FormatBps(up.MeanBps)));
        lines.Add(Row("Latency", $"{F(r.PingAvgMs)} {term.Dim("avg")}  " +
            $"{CliHumanFormatter.Grade(term, r.PingP95Ms, 10, 30, 100, F(r.PingP95Ms))} {term.Dim("p95 ms")}"));
        lines.Add(Row("Jitter", $"{CliHumanFormatter.Grade(term, r.JitterMs, 5, 15, 30, F(r.JitterMs))} ms   " +
            $"{term.Dim("probe loss")} {CliHumanFormatter.Grade(term, r.LossPct, 0.05, 0.2, 1.0, F(r.LossPct) + "%")}"));
        if (r.RttUnderLoadMs is { } rtt)
            lines.Add(Row("Under load", $"p95 {CliHumanFormatter.Grade(term, rtt.P95Ms, 15, 40, 120, F(rtt.P95Ms))} ms {sym.Dot} {rtt.SpikesCount} spikes"));
        if (r.Udp is { } u)
            lines.Add(Row("Datagrams", $"{u.PacketsReceived}/{u.PacketsSent} {sym.Dot} lost {F(u.LossPct)}% {sym.Dot} jitter {F(u.JitterMs)} ms"));
        if (CliHumanFormatter.Bufferbloat(r) is { } bloat)
            lines.Add($"{term.Yellow(sym.Warn)} bufferbloat {sym.Dot} {F(bloat.LoadedMs)} ms under load (baseline {F(bloat.BaselineMs)})");
        var q = CliQuality.Grade(r);
        lines.Add($"{CliHumanFormatter.QualityGlyph(term, q)} {CliQuality.Paint(term, q)} {sym.Dot} {term.Dim(CliHumanFormatter.VerdictCopy(q))}");
        return lines;
    }

    private static List<string> HistoryLines(TuiState s, CliTerminal term, int inner)
    {
        var lines = CliTable.RenderHeader(term, HistoryColumns).Split(Environment.NewLine).ToList();
        if (s.History.Count == 0)
        {
            lines.Add(term.Dim("no measurements yet"));
            return lines;
        }
        foreach (var h in s.History)
        {
            if (h.Error is { } error)
            {
                lines.Add(CliTable.RenderRow(term, HistoryColumns.Take(3).ToArray(),
                    [h.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), h.Peer, h.Mode]) +
                    "  " + (error == "cancelled" ? term.Dim(error) : term.Red(error)));
                continue;
            }
            lines.Add(CliTable.RenderRow(term, HistoryColumns,
            [
                h.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                h.Peer,
                h.Mode,
                h.DownBps == 0 ? term.Dim("-") : CliHumanFormatter.FormatBps(h.DownBps),
                h.UpBps == 0 ? term.Dim("-") : CliHumanFormatter.FormatBps(h.UpBps),
                $"{h.P95Ms.ToString("F1", CultureInfo.InvariantCulture)} ms",
                $"{h.LossPct.ToString("F1", CultureInfo.InvariantCulture)}%",
                h.Quality is { } q ? CliQuality.Paint(term, q) : "",
            ]));
        }
        return lines;
    }

    private static List<string> HelpLines(CliTerminal term)
    {
        string K(string keys, string what) => $"{term.Cyan(keys.PadRight(12))}{what}";
        return
        [
            K($"{term.Symbols.Up} {term.Symbols.Down}  j k", "select peer"),
            K("enter", "measure the selected peer"),
            K("esc  x", "cancel the running test"),
            K("d", "direction: down, up, bidir"),
            K("u", "transport: tcp / udp"),
            K("s", "streams: 1, 2, 4, 8"),
            K("t", "duration: 5, 10, 30 s"),
            K("b", "udp rate per stream (0 = unlimited)"),
            K("w", "watch: repeat after each test"),
            K("a", "add a host by address"),
            K("r", "rescan the network"),
            K("q  ctrl+c", "quit"),
            "",
            term.Dim("any key closes this help"),
        ];
    }

    /// <summary>Caja con borde y título; siempre <paramref name="height"/> líneas de <paramref name="width"/>.</summary>
    private static string[] Box(CliTerminal term, string title, int width, int height, IReadOnlyList<string> content, bool focused)
    {
        var s = term.Symbols;
        var inner = width - 4;
        var lines = new string[height];
        var titleText = $"{s.Horizontal} {title} ";
        var fill = Math.Max(0, width - 2 - CliText.VisibleLength(titleText));
        string Border(string text) => focused ? term.Cyan(text) : term.Dim(text);
        lines[0] = Border(s.TopLeft) + Border(s.Horizontal) + " " + term.Bold(title) + " " + Border(Repeat(s.Horizontal, fill)) + Border(s.TopRight);
        for (var i = 1; i < height - 1; i++)
        {
            var text = i - 1 < content.Count ? content[i - 1] : "";
            lines[i] = Border(s.Vertical) + " " + CliText.Fit(text.Replace('\r', ' ').Replace('\n', ' '), inner, ellipsis: s.Ellipsis) + " " + Border(s.Vertical);
        }
        lines[height - 1] = Border(s.BottomLeft + Repeat(s.Horizontal, width - 2) + s.BottomRight);
        return lines;
    }

    private static string[] TooSmall(CliTerminal term, int width, int height)
    {
        var lines = Enumerable.Repeat(new string(' ', Math.Max(0, width)), Math.Max(0, height)).ToArray();
        if (height == 0 || width == 0) return lines;
        var msg = $"terminal too small ({width}x{height}); need {MinWidth}x{MinHeight}";
        var hint = "resize the window, or press q to quit";
        lines[height / 2] = Fit(Center(msg, width), width, term);
        if (height / 2 + 1 < height) lines[height / 2 + 1] = Fit(term.Dim(Center(hint, width)), width, term);
        return lines;
    }

    private static string Center(string text, int width) =>
        new string(' ', Math.Max(0, (width - text.Length) / 2)) + text;

    private static string Fit(string text, int width, CliTerminal term) =>
        CliText.Fit(text, width, ellipsis: term.Symbols.Ellipsis);

    private static string Pad(string text, int width, CliTerminal term) => Fit(text, Math.Max(0, width), term);

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, Math.Max(0, n)));
}
