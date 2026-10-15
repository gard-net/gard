using Gard.Core.Protocol;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>
/// LSP/1.1 — RTT bajo carga via <c>DATA_BINARY_ECHO</c> (0x04). Mide latencia
/// sujeta a la misma cola TCP que el throughput, a diferencia del ping de
/// control que mide RTT en vacío.
/// </summary>
public static class RttUnderLoadBuilder
{
    /// <summary>
    /// Construye un snapshot desde un array de RTTs en ms. El caller debe
    /// pasarlos en orden temporal (no ordenados) para poder calcular la
    /// baseline del primer cuartil.
    /// </summary>
    public static RttUnderLoadStats FromRttMs(IReadOnlyList<double> rttMs)
    {
        if (rttMs.Count == 0) return RttUnderLoadStats.Empty;

        var sorted = rttMs.ToArray();
        Array.Sort(sorted);
        var minV = sorted[0];
        var maxV = sorted[^1];
        var median = Statistics.Percentile(sorted, 0.5);
        var p95 = Statistics.Percentile(sorted, 0.95);
        var stdev = Statistics.Stddev(rttMs);

        // Baseline: primer cuartil en orden de llegada (no ordenado).
        var quartile = Math.Max(1, rttMs.Count / 4);
        var firstQuartile = new double[quartile];
        for (var i = 0; i < quartile; i++) firstQuartile[i] = rttMs[i];
        var baseline = Statistics.Percentile(firstQuartile, 0.5);

        // Spike: RTT > 3× baseline (spec §4); 3× median sólo si baseline == 0.
        // Max(baseline, median) subcontaba spikes justo bajo bufferbloat,
        // cuando la mediana ya está inflada respecto de la baseline.
        var spikeThreshold = (baseline > 0 ? baseline : median) * 3.0;
        var spikes = 0;
        for (var i = 0; i < rttMs.Count; i++)
        {
            if (rttMs[i] > spikeThreshold) spikes++;
        }

        return new RttUnderLoadStats
        {
            Samples = rttMs.Count,
            MinMs = minV,
            MedianMs = median,
            P95Ms = p95,
            MaxMs = maxV,
            StdevMs = stdev,
            BaselineMedianMs = baseline,
            SpikesCount = spikes,
        };
    }
}

/// <summary>
/// Colector thread-safe de muestras de RTT bajo carga. Los loops de recepción
/// de echo acumulan muestras; al final del test el cliente invoca
/// <see cref="Snapshot"/> para embeberlo en el <c>ResultBody</c>.
/// </summary>
public sealed class RttSampleCollector
{
    private readonly object _gate = new();
    private readonly List<double> _samplesMs = new();
    private volatile bool _accepting = true;

    /// <summary>
    /// Si es <c>false</c>, <see cref="Add"/> descarta muestras. El cliente lo
    /// apaga durante warmup y gap: la baseline debe salir de muestras post-warmup.
    /// </summary>
    public bool Accepting
    {
        get => _accepting;
        set => _accepting = value;
    }

    public void Add(double rttMs)
    {
        if (!_accepting) return;
        lock (_gate) _samplesMs.Add(rttMs);
    }

    public int Count
    {
        get { lock (_gate) return _samplesMs.Count; }
    }

    public RttUnderLoadStats Snapshot()
    {
        double[] copy;
        lock (_gate) copy = _samplesMs.ToArray();
        return RttUnderLoadBuilder.FromRttMs(copy);
    }
}
