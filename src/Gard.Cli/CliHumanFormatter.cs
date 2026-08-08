using Gard.Core.Models;
using Gard.Core.Protocol;
using System.Globalization;

namespace Gard.Cli;

public static class CliHumanFormatter
{
    public static string Format(TestResult r)
        => Format(r, new CliTerminal(new CliStyleOptions(true, true, "plain")));

    public static string Format(TestResult r, CliTerminal term)
    {
        if (term.Rich) return FormatRich(r, term);

        var lines = new List<string>
        {
            "Gard test result",
            "",
            $"Session:   {r.SessionId}",
            $"Peer:      {r.PeerName} ({r.PeerPlatform.ToString().ToLowerInvariant()})",
            $"Protocol:  LSP/{r.ProtocolVersion}",
            $"Direction: {r.Direction.ToString().ToLowerInvariant()}",
            $"Streams:   {r.Streams}",
            $"Duration:  {r.DurationS:F2}s",
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
            $"  Ping:    min {r.PingMinMs:F2} ms, avg {r.PingAvgMs:F2} ms, p95 {r.PingP95Ms:F2} ms, max {r.PingMaxMs:F2} ms",
            $"  Jitter:  {r.JitterMs:F2} ms",
            $"  Loss:    {r.LossPct:F2}% ({r.PingSamples} samples)",
        ]);

        if (r.RttUnderLoadMs is { } rtt)
        {
            lines.AddRange([
                "",
                "RTT under load",
                $"  Median:  {rtt.MedianMs:F2} ms",
                $"  P95:     {rtt.P95Ms:F2} ms",
                $"  Spikes:  {rtt.SpikesCount} ({rtt.Samples} samples)",
            ]);
        }

        if (r.Udp is { } u)
        {
            lines.AddRange([
                "",
                "UDP",
                $"  Packets: sent {u.PacketsSent}, received {u.PacketsReceived}, lost {u.PacketsLost} ({u.LossPct:F2}%)",
                $"  Reorder: {u.ReorderCount} ({u.ReorderPct:F2}%)",
                $"  Dupes:   {u.DuplicateCount}",
                $"  Jitter:  {u.JitterMs:F2} ms",
            ]);
            if (u.TargetBitrateBps > 0)
                lines.Add($"  Target:  {FormatBps(u.TargetBitrateBps)}, miss {u.BitrateMissPct:F2}%");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRich(TestResult r, CliTerminal term)
    {
        var quality = CliQuality.Grade(r);
        var declaredTarget = r.Udp?.TargetBitrateBps > 0 ? r.Udp.TargetBitrateBps : 0UL;
        var target = Math.Max(Math.Max(r.MeanBps, r.PeakBps), declaredTarget);
        if (target == 0) target = r.MeanBps;
        var ratio = target == 0 ? 0 : r.MeanBps / (double)target;
        var latencyRatio = 1.0 - Math.Clamp(r.PingP95Ms / 100.0, 0, 1);
        var lossRatio = 1.0 - Math.Clamp(r.LossPct / 5.0, 0, 1);
        var jitterRatio = 1.0 - Math.Clamp(r.JitterMs / 30.0, 0, 1);

        var lines = new List<string>
        {
            BoxTop(),
            BoxLine($"GARD LINK METER  {GlyphFor(quality)}  {PaintGrade(term, quality, uppercase: true)}"),
            BoxLine($"target {r.PeerName}   lsp/{r.ProtocolVersion}   {r.Direction.ToString().ToLowerInvariant()}   {r.Streams} stream{(r.Streams == 1 ? "" : "s")}   {r.DurationS.ToString("F0", CultureInfo.InvariantCulture)}s"),
            BoxRule(),
            BoxLine(term.Cyan("THROUGHPUT") + $"  MEAN {FormatBps(r.MeanBps)}   PEAK {FormatBps(r.PeakBps)}"),
            BoxLine(PaintGauge(term, ratio, 42) + "  " + Percent(ratio)),
            BoxLine(SignalTrace(term, ratio)),
        };

        if (r.ThroughputDown is { } down)
        {
            var downRatio = down.PeakBps == 0 ? 0 : down.MeanBps / (double)down.PeakBps;
            lines.Add(BoxLine($"▼ DOWN  {PaintGauge(term, downRatio, 24)}  mean {FormatBps(down.MeanBps),10}  peak {FormatBps(down.PeakBps),10}"));
        }
        if (r.ThroughputUp is { } up)
        {
            var upRatio = up.PeakBps == 0 ? 0 : up.MeanBps / (double)up.PeakBps;
            lines.Add(BoxLine($"▲ UP    {PaintGauge(term, upRatio, 24)}  mean {FormatBps(up.MeanBps),10}  peak {FormatBps(up.PeakBps),10}"));
        }

        lines.AddRange([
            BoxRule(),
            BoxLine(Card("LATENCY", $"p95 {Fmt2(r.PingP95Ms)} ms", latencyRatio, term) + "  " + Card("JITTER", $"{Fmt2(r.JitterMs)} ms", jitterRatio, term)),
            BoxLine(Card("LOSS", $"{Fmt2(r.LossPct)}%", lossRatio, term) + $"  samples {r.PingSamples}"),
        ]);

        if (r.RttUnderLoadMs is { } rtt)
        {
            var rttRatio = 1.0 - Math.Clamp(rtt.P95Ms / 150.0, 0, 1);
            lines.Add(BoxLine(Card("RTT UNDER LOAD", $"p95 {Fmt2(rtt.P95Ms)} ms · spikes {rtt.SpikesCount}", rttRatio, term)));
        }

        if (r.Udp is { } u)
        {
            var udpRatio = 1.0 - Math.Clamp(u.LossPct / 5.0, 0, 1);
            lines.Add(BoxLine(Card("UDP DATAGRAMS", $"rx {u.PacketsReceived}/{u.PacketsSent} · lost {u.PacketsLost} · dup {u.DuplicateCount}", udpRatio, term)));
            lines.Add(BoxLine($"          reorder {Fmt2(u.ReorderPct)}%   jitter {Fmt2(u.JitterMs)} ms" +
                (u.TargetBitrateBps > 0 ? $"   target {FormatBps(u.TargetBitrateBps)} miss {Fmt2(u.BitrateMissPct)}%" : "")));
        }

        lines.AddRange([
            BoxRule(),
            BoxLine($"VERDICT  {GlyphFor(quality)} {CliQuality.Paint(term, quality)}  · local link is {VerdictCopy(quality)}"),
            BoxBottom(),
        ]);
        return string.Join(Environment.NewLine, lines);
    }

    private const int DashboardWidth = 76;
    private const int DashboardInnerWidth = DashboardWidth - 4;

    private static string BoxTop() => "╔" + new string('═', DashboardWidth - 2) + "╗";
    private static string BoxBottom() => "╚" + new string('═', DashboardWidth - 2) + "╝";
    private static string BoxRule() => "║ " + new string('─', DashboardInnerWidth) + " ║";

    private static string BoxLine(string content)
    {
        content = StripNewlines(content);
        if (VisibleLength(content) > DashboardInnerWidth)
            content = TruncateVisible(content, DashboardInnerWidth);
        return "║ " + content + new string(' ', Math.Max(0, DashboardInnerWidth - VisibleLength(content))) + " ║";
    }

    private static string PaintGauge(CliTerminal term, double ratio, int width)
    {
        var gauge = CliTable.Gauge(ratio, width);
        var fill = gauge.Count(c => c == '█');
        var full = gauge[..fill];
        var empty = gauge[fill..];
        var paint = ratio switch
        {
            >= 0.85 => term.Green(full),
            >= 0.65 => term.Cyan(full),
            >= 0.35 => term.Yellow(full),
            _ => term.Red(full),
        };
        return paint + term.Dim(empty);
    }

    private static string Card(string title, string value, double ratio, CliTerminal term)
        => "┌─ " + term.Bold(title) + " " + PaintGauge(term, ratio, 10) + " " + value + " └";

    private static string SignalTrace(CliTerminal term, double ratio)
    {
        var chars = ratio switch
        {
            >= 0.85 => "▁▂▃▄▅▆▇█▇▆▅▄▃▂▁",
            >= 0.65 => "▁▃▅▇▆▄▅▇▅▃▁▃▅▆▇",
            >= 0.35 => "▁▆▂▇▃▅▂▆▁▅▂▇▃▅▁",
            _ => "▁▇▁▆▁▇▂▁▆▁▂▇▁▃▁",
        };
        return term.Dim("quality signature ") + term.Purple(chars) + term.Dim(" based on this result");
    }

    private static string Percent(double ratio)
    {
        if (!double.IsFinite(ratio)) ratio = 0;
        return (Math.Clamp(ratio, 0, 1) * 100.0).ToString("F0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Fmt2(double value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string PaintGrade(CliTerminal term, LinkQuality quality, bool uppercase = false)
    {
        var label = CliQuality.Label(quality);
        if (uppercase) label = label.ToUpperInvariant();
        return quality switch
        {
            LinkQuality.Excellent => term.Green(label),
            LinkQuality.Good => term.Cyan(label),
            LinkQuality.Degraded => term.Yellow(label),
            _ => term.Red(label),
        };
    }

    private static string GlyphFor(LinkQuality quality) => quality switch
    {
        LinkQuality.Excellent => "●",
        LinkQuality.Good => "●",
        LinkQuality.Degraded => "◐",
        _ => "○",
    };

    private static string VerdictCopy(LinkQuality quality) => quality switch
    {
        LinkQuality.Excellent => "excellent for demanding local workloads",
        LinkQuality.Good => "healthy with minor headroom limits",
        LinkQuality.Degraded => "usable but showing latency/loss pressure",
        _ => "unstable; investigate Wi-Fi, cabling or congestion",
    };

    private static string StripNewlines(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ');

    private static int VisibleLength(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            count++;
        }
        return count;
    }

    private static string TruncateVisible(string text, int width)
    {
        var sb = new System.Text.StringBuilder();
        var count = 0;
        var sawAnsi = false;
        for (var i = 0; i < text.Length && count < width - 1; i++)
        {
            if (text[i] == '\u001b')
            {
                sawAnsi = true;
                while (i < text.Length)
                {
                    sb.Append(text[i]);
                    if (text[i] == 'm') break;
                    i++;
                }
                continue;
            }
            sb.Append(text[i]);
            count++;
        }
        if (sawAnsi) sb.Append("\u001b[0m");
        sb.Append('…');
        return sb.ToString();
    }

    public static string FormatBps(ulong bps)
    {
        if (bps >= 1_000_000_000_000UL) return Format(bps / 1_000_000_000_000.0, "Tb/s");
        if (bps >= 1_000_000_000UL) return Format(bps / 1_000_000_000.0, "Gb/s");
        if (bps >= 1_000_000UL) return Format(bps / 1_000_000.0, "Mb/s");
        if (bps >= 1_000UL) return Format(bps / 1_000.0, "Kb/s");
        return $"{bps} b/s";
    }

    private static string Format(double value, string unit)
        => $"{value.ToString("F2", CultureInfo.InvariantCulture)} {unit}";

    public static string FormatResultBodySummary(ResultBody r)
        => $"mean {FormatBps(r.Throughput.MeanBps)}, peak {FormatBps(r.Throughput.PeakBps)}";
}
