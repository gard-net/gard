using System.Globalization;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

public static class CliCsvFormatter
{
    public static string[] Columns =>
    [
        "label", "direction", "streams", "duration_s", "warmup_s", "payload", "bidir_mode",
        "transport", "target_mbps",
        "mean_mbps", "peak_mbps",
        "up_mean_mbps", "up_peak_mbps", "down_mean_mbps", "down_peak_mbps",
        "ping_min_ms", "ping_avg_ms", "ping_max_ms", "ping_p95_ms",
        "jitter_ms", "loss_pct", "ping_samples",
        "rtt_load_median_ms", "rtt_load_p95_ms", "rtt_load_spikes",
        "udp_packets_sent", "udp_packets_recv", "udp_loss_pct", "udp_reorder_pct", "udp_dup",
        "udp_jitter_ms", "udp_bitrate_miss_pct",
    ];

    public static string Header() => string.Join(",", Columns);

    public static string Format(TestResult r, TestParameters p, string label)
    {
        static string Mbps(ulong bps) => (bps / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture);
        static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

        string upMean = r.ThroughputUp is { } u ? Mbps(u.MeanBps) : "";
        string upPeak = r.ThroughputUp is { } u2 ? Mbps(u2.PeakBps) : "";
        string downMean = r.ThroughputDown is { } d ? Mbps(d.MeanBps) : "";
        string downPeak = r.ThroughputDown is { } d2 ? Mbps(d2.PeakBps) : "";
        string rttMed = r.RttUnderLoadMs is { } m ? F(m.MedianMs) : "";
        string rttP95 = r.RttUnderLoadMs is { } m2 ? F(m2.P95Ms) : "";
        string rttSpk = r.RttUnderLoadMs is { } m3 ? m3.SpikesCount.ToString(CultureInfo.InvariantCulture) : "";
        string udpSent = r.Udp is { } u1 ? u1.PacketsSent.ToString(CultureInfo.InvariantCulture) : "";
        string udpRecv = r.Udp is { } u2b ? u2b.PacketsReceived.ToString(CultureInfo.InvariantCulture) : "";
        string udpLoss = r.Udp is { } u3 ? F(u3.LossPct) : "";
        string udpReord = r.Udp is { } u4 ? F(u4.ReorderPct) : "";
        string udpDup = r.Udp is { } u5 ? u5.DuplicateCount.ToString(CultureInfo.InvariantCulture) : "";
        string udpJit = r.Udp is { } u6 ? F(u6.JitterMs) : "";
        string udpMiss = r.Udp is { } u7 ? F(u7.BitrateMissPct) : "";
        string targetMbps = p.Transport == TestTransport.Udp
            ? (p.TargetBitrateBps / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture)
            : "";

        var cells = new[]
        {
            label,
            r.Direction.ToString().ToLowerInvariant(),
            r.Streams.ToString(CultureInfo.InvariantCulture),
            r.DurationS.ToString("F2", CultureInfo.InvariantCulture),
            p.WarmupS.ToString("F2", CultureInfo.InvariantCulture),
            p.PayloadSize.ToString(CultureInfo.InvariantCulture),
            p.BidirMode?.ToString().ToLowerInvariant() ?? "",
            p.Transport.ToString().ToLowerInvariant(),
            targetMbps,
            Mbps(r.MeanBps), Mbps(r.PeakBps),
            upMean, upPeak, downMean, downPeak,
            F(r.PingMinMs), F(r.PingAvgMs), F(r.PingMaxMs), F(r.PingP95Ms),
            F(r.JitterMs), F(r.LossPct), r.PingSamples.ToString(CultureInfo.InvariantCulture),
            rttMed, rttP95, rttSpk,
            udpSent, udpRecv, udpLoss, udpReord, udpDup, udpJit, udpMiss,
        };
        return string.Join(",", cells.Select(Escape));
    }

    public static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
