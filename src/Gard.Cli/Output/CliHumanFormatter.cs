using System.Globalization;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// Reporte humano de un test. En un TTY rico: panel con borde redondeado que
/// se adapta al ancho del terminal, métricas coloreadas por umbral y un
/// sparkline real del throughput por intervalos. En modo plano: líneas
/// "Clave: valor" estables para logs.
/// </summary>
public static class CliHumanFormatter
{
    private const int MaxPanelWidth = 84;
    private const int MinPanelWidth = 56;
    private const int LabelWidth = 13;

    public static string Format(TestResult r) => Format(r, new CliTerminal(CliStyleOptions.PlainText));

    /// <param name="history">Throughput instantáneo visto en vivo; si falta se usan
    /// los intervalos del resultado (TCP con <c>interval_reporting</c>).</param>
    public static string Format(TestResult r, CliTerminal term, IReadOnlyList<double>? history = null)
        => term.Rich ? FormatRich(r, term, history) : FormatPlain(r);

    private static string FormatPlain(TestResult r)
    {
        var lines = new List<string>
        {
            "Gard test result",
            "",
            $"Session:   {r.SessionId}",
            $"Peer:      {r.PeerName} ({Lower(r.PeerPlatform)})",
            $"Protocol:  LSP/{r.ProtocolVersion}",
            $"Direction: {Lower(r.Direction)}",
            $"Streams:   {r.Streams}",
            $"Duration:  {F2(r.DurationS)}s",
            "",
            "Throughput",
            $"  Mean:    {FormatBps(r.MeanBps)}",
            $"  Peak:    {FormatBps(r.PeakBps)}",
        };
        if (r.ThroughputUp is { } up)
            lines.Add($"  Up:      mean {FormatBps(up.MeanBps)}, peak {FormatBps(up.PeakBps)}");
        if (r.ThroughputDown is { } down)
            lines.Add($"  Down:    mean {FormatBps(down.MeanBps)}, peak {FormatBps(down.PeakBps)}");

        lines.AddRange([
            "",
            "Latency",
            $"  Ping:    min {F2(r.PingMinMs)} ms, avg {F2(r.PingAvgMs)} ms, p95 {F2(r.PingP95Ms)} ms, max {F2(r.PingMaxMs)} ms",
            $"  Jitter:  {F2(r.JitterMs)} ms",
            $"  Loss:    {F2(r.LossPct)}% ({r.PingSamples} samples)",
        ]);
        if (r.RttUnderLoadMs is { } rtt)
        {
            lines.AddRange([
                "",
                "RTT under load",
                $"  Median:  {F2(rtt.MedianMs)} ms",
                $"  P95:     {F2(rtt.P95Ms)} ms",
                $"  Spikes:  {rtt.SpikesCount} ({rtt.Samples} samples)",
            ]);
        }
        if (r.Udp is { } u)
        {
            lines.AddRange([
                "",
                "UDP",
                $"  Packets: sent {u.PacketsSent}, received {u.PacketsReceived}, lost {u.PacketsLost} ({F2(u.LossPct)}%)",
                $"  Reorder: {u.ReorderCount} ({F2(u.ReorderPct)}%)",
                $"  Dupes:   {u.DuplicateCount}",
                $"  Jitter:  {F2(u.JitterMs)} ms",
            ]);
            if (u.TargetBitrateBps > 0)
                lines.Add($"  Target:  {FormatBps(u.TargetBitrateBps)}, miss {F2(u.BitrateMissPct)}%");
        }
        if (Bufferbloat(r) is { } bloat)
            lines.AddRange(["", $"Bufferbloat: latency rises to {F2(bloat.LoadedMs)} ms under load (baseline {F2(bloat.BaselineMs)} ms)"]);
        lines.AddRange(["", $"Quality:   {CliQuality.Label(CliQuality.Grade(r))}"]);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRich(TestResult r, CliTerminal term, IReadOnlyList<double>? history)
    {
        var s = term.Symbols;
        var width = Math.Clamp(term.Width, MinPanelWidth, MaxPanelWidth);
        var inner = width - 4;
        var quality = CliQuality.Grade(r);
        var panel = new Panel(term, width);

        var transport = r.Udp is null ? "tcp" : "udp";
        panel.Title($"gard {s.Horizontal} {r.PeerName}", $"{QualityGlyph(term, quality)} {CliQuality.Paint(term, quality)}");
        panel.Line(term.Dim(string.Join($" {s.Dot} ",
            Lower(r.Direction), transport, Plural(r.Streams, "stream"),
            $"{r.DurationS.ToString("0.0", CultureInfo.InvariantCulture)}s",
            $"LSP/{r.ProtocolVersion}", Lower(r.PeerPlatform))));
        panel.Blank();

        panel.Row("Throughput",
            $"{term.Bold(FormatBps(r.MeanBps))} {term.Dim("mean")}   {FormatBps(r.PeakBps)} {term.Dim("peak")}");
        var series = history is { Count: > 1 } ? history
            : r.Intervals?.Samples.Select(x => (double)x.Bps).ToArray();
        if (series is { Count: > 1 })
            panel.Row("", term.Purple(CliTable.Sparkline(series, inner - LabelWidth, s)));
        if (r.ThroughputDown is { } down)
            panel.Row($"  {s.Down} down", $"{FormatBps(down.MeanBps)} {term.Dim("mean")}   {FormatBps(down.PeakBps)} {term.Dim("peak")}");
        if (r.ThroughputUp is { } up)
            panel.Row($"  {s.Up} up", $"{FormatBps(up.MeanBps)} {term.Dim("mean")}   {FormatBps(up.PeakBps)} {term.Dim("peak")}");
        panel.Blank();

        panel.Row("Latency",
            $"{F2(r.PingMinMs)} {term.Dim("min")}  {F2(r.PingAvgMs)} {term.Dim("avg")}  " +
            $"{Grade(term, r.PingP95Ms, 10, 30, 100, F2(r.PingP95Ms))} {term.Dim("p95")}  {F2(r.PingMaxMs)} {term.Dim("max ms")}");
        panel.Row("Jitter", $"{Grade(term, r.JitterMs, 5, 15, 30, F2(r.JitterMs))} ms");
        // Son pings de control sin respuesta en 500 ms, no paquetes de datos
        // perdidos: bajo bufferbloat suben aunque la red no pierda nada.
        panel.Row("Probe loss", $"{Grade(term, r.LossPct, 0.05, 0.2, 1.0, F2(r.LossPct) + "%")}  {term.Dim($"of {r.PingSamples} pings (no reply in 500 ms)")}");
        if (r.RttUnderLoadMs is { } rtt)
        {
            panel.Row("Under load",
                $"p95 {Grade(term, rtt.P95Ms, 15, 40, 120, F2(rtt.P95Ms))} ms {s.Dot} median {F2(rtt.MedianMs)} ms {s.Dot} {Plural(rtt.SpikesCount, "spike")}");
        }
        if (r.Udp is { } u)
        {
            panel.Row("Datagrams",
                $"{u.PacketsReceived}/{u.PacketsSent} received {s.Dot} {Grade(term, u.LossPct, 0.05, 0.2, 1.0, $"{u.PacketsLost} lost")} " +
                $"{s.Dot} {u.DuplicateCount} dup {s.Dot} {F2(u.ReorderPct)}% reorder");
            if (u.TargetBitrateBps > 0)
                panel.Row("Target", $"{FormatBps(u.TargetBitrateBps)} per stream {s.Dot} miss {F2(u.BitrateMissPct)}%");
        }
        panel.Blank();
        if (Bufferbloat(r) is { } bloat)
        {
            panel.Line($"{term.Yellow(s.Warn)} {term.Bold("Bufferbloat")} {s.Dot} latency rises to {F2(bloat.LoadedMs)} ms under load " +
                term.Dim($"(baseline {F2(bloat.BaselineMs)} ms)"));
        }
        panel.Line($"{QualityGlyph(term, quality)} {term.Bold(Capitalize(CliQuality.Label(quality)))} {s.Dot} {VerdictCopy(quality)}");
        panel.Bottom();
        return panel.ToString();
    }

    /// <summary>
    /// Bufferbloat: el RTT bajo carga (p95 de los echo sobre el plano de datos)
    /// supera 100 ms y es al menos 5× la línea base. Explica "probe loss" alto
    /// en Wi-Fi o routers con colas grandes aunque no se pierdan datos.
    /// </summary>
    public static (double BaselineMs, double LoadedMs)? Bufferbloat(TestResult r)
    {
        if (r.RttUnderLoadMs is not { Samples: > 0 } rtt) return null;
        var baseline = rtt.BaselineMedianMs > 0 ? rtt.BaselineMedianMs : r.PingMinMs;
        if (baseline <= 0) return null;
        return rtt.P95Ms >= 100 && rtt.P95Ms >= 5 * baseline ? (baseline, rtt.P95Ms) : null;
    }

    private static string Grade(CliTerminal term, double value, double good, double ok, double warn, string text) =>
        value <= good ? term.Green(text)
        : value <= ok ? term.Cyan(text)
        : value <= warn ? term.Yellow(text)
        : term.Red(text);

    private static string QualityGlyph(CliTerminal term, LinkQuality q) => q switch
    {
        LinkQuality.Excellent => term.Green(term.Symbols.Ok),
        LinkQuality.Good => term.Cyan(term.Symbols.Ok),
        LinkQuality.Degraded => term.Yellow(term.Symbols.Warn),
        _ => term.Red(term.Symbols.Fail),
    };

    private static string VerdictCopy(LinkQuality quality) => quality switch
    {
        LinkQuality.Excellent => "ready for demanding local workloads",
        LinkQuality.Good => "healthy, with minor headroom limits",
        LinkQuality.Degraded => "usable, but showing latency or loss pressure",
        _ => "unstable; check Wi-Fi, cabling or congestion",
    };

    public static string FormatBps(ulong bps)
    {
        if (bps >= 1_000_000_000_000UL) return Unit(bps / 1_000_000_000_000.0, "Tb/s");
        if (bps >= 1_000_000_000UL) return Unit(bps / 1_000_000_000.0, "Gb/s");
        if (bps >= 1_000_000UL) return Unit(bps / 1_000_000.0, "Mb/s");
        if (bps >= 1_000UL) return Unit(bps / 1_000.0, "Kb/s");
        return $"{bps} b/s";
    }

    public static string FormatResultBodySummary(ResultBody r)
        => $"mean {FormatBps(r.Throughput.MeanBps)}, peak {FormatBps(r.Throughput.PeakBps)}";

    private static string Unit(double value, string unit) => $"{F2(value)} {unit}";
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Panel con borde que mantiene todas las líneas al mismo ancho visible.</summary>
    private sealed class Panel(CliTerminal term, int width)
    {
        private readonly List<string> _lines = new();
        private readonly CliSymbols _s = term.Symbols;
        private int Inner => width - 4;

        public void Title(string left, string right)
        {
            var leftPart = $"{_s.TopLeft}{_s.Horizontal} {term.Bold(left)} ";
            var rightPart = $" {right} {_s.Horizontal}{_s.TopRight}";
            var fill = Math.Max(1, width - CliText.VisibleLength(leftPart) - CliText.VisibleLength(rightPart));
            _lines.Add(leftPart + term.Dim(Repeat(_s.Horizontal, fill)) + rightPart);
        }

        public void Line(string content)
        {
            content = content.Replace('\r', ' ').Replace('\n', ' ');
            var fitted = CliText.Fit(content, Inner, ellipsis: _s.Ellipsis);
            _lines.Add($"{term.Dim(_s.Vertical)} {fitted} {term.Dim(_s.Vertical)}");
        }

        public void Row(string label, string value) =>
            Line(term.Dim(label.PadRight(LabelWidth)) + value);

        public void Blank() => Line("");

        public void Bottom() =>
            _lines.Add(term.Dim(_s.BottomLeft + Repeat(_s.Horizontal, width - 2) + _s.BottomRight));

        public override string ToString() => string.Join(Environment.NewLine, _lines);

        private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, Math.Max(0, n)));
    }
}
