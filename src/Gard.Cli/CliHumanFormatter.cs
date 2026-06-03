using Gard.Core.Models;
using Gard.Core.Protocol;
using System.Globalization;

namespace Gard.Cli;

public static class CliHumanFormatter
{
    public static string Format(TestResult r)
    {
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
