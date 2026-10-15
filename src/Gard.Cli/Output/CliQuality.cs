using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

public enum LinkQuality
{
    Excellent,
    Good,
    Degraded,
    Poor,
}

public static class CliQuality
{
    public static LinkQuality Grade(TestResult result)
        => Grade(result.MeanBps, result.PingP95Ms, result.LossPct);

    public static LinkQuality Grade(ResultBody result)
        => Grade(result.Throughput.MeanBps, result.LatencyMs.P95, result.LossPct);

    public static LinkQuality Grade(ulong meanBps, double latencyP95Ms, double lossPct)
    {
        var mbps = meanBps / 1_000_000d;
        if (lossPct <= 0.05 && latencyP95Ms <= 10 && mbps >= 500) return LinkQuality.Excellent;
        if (lossPct <= 0.2 && latencyP95Ms <= 30 && mbps >= 100) return LinkQuality.Good;
        if (lossPct <= 1.0 && latencyP95Ms <= 100 && mbps >= 20) return LinkQuality.Degraded;
        return LinkQuality.Poor;
    }

    public static string Label(LinkQuality quality) => quality switch
    {
        LinkQuality.Excellent => "excellent",
        LinkQuality.Good => "good",
        LinkQuality.Degraded => "degraded",
        _ => "poor",
    };

    public static string Paint(CliTerminal term, LinkQuality quality)
    {
        var label = Label(quality);
        return quality switch
        {
            LinkQuality.Excellent => term.Green(label),
            LinkQuality.Good => term.Cyan(label),
            LinkQuality.Degraded => term.Yellow(label),
            _ => term.Red(label),
        };
    }
}
