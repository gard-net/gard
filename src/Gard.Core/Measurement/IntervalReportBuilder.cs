using Gard.Core.Protocol;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>
/// LSP/1.1 — constructor de <see cref="IntervalReport"/> a partir de ventanas
/// cronológicas. Sólo se habilita si ambos peers anuncian
/// <see cref="Capabilities.IntervalReporting"/>.
/// </summary>
public static class IntervalReportBuilder
{
    /// <summary>
    /// Construye el reporte desde un array de ventanas en orden cronológico.
    /// </summary>
    public static IntervalReport FromSamples(int windowMs, IReadOnlyList<IntervalSample> samples)
    {
        if (samples.Count == 0)
        {
            return new IntervalReport
            {
                WindowMs = windowMs,
                Samples = Array.Empty<IntervalSample>(),
                Stats = new IntervalStats(),
            };
        }

        var bpsValues = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++) bpsValues[i] = samples[i].Bps;
        var sorted = (double[])bpsValues.Clone();
        Array.Sort(sorted);

        var stats = new IntervalStats
        {
            MedianBps = (ulong)Statistics.Percentile(sorted, 0.5),
            P95Bps = (ulong)Statistics.Percentile(sorted, 0.95),
            StdevBps = (ulong)Statistics.Stddev(bpsValues),
            MinBps = (ulong)sorted[0],
            MaxBps = (ulong)sorted[^1],
        };

        return new IntervalReport
        {
            WindowMs = windowMs,
            Samples = samples,
            Stats = stats,
        };
    }
}
