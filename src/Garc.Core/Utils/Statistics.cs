namespace Garc.Core.Utils;

/// <summary>
/// Utilidades numéricas para el motor de medición. Deben producir resultados
/// idénticos (hasta errores de punto flotante) a <c>Statistics.swift</c> de la
/// app Apple para mantener consistencia entre plataformas.
/// </summary>
public static class Statistics
{
    public static double Mean(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var sum = 0.0;
        foreach (var v in xs) sum += v;
        return sum / xs.Count;
    }

    /// <summary>
    /// Desviación estándar poblacional (n en el denominador). Spec §5.4: jitter
    /// = stddev de RTTs, sin precisar poblacional vs muestral; usamos
    /// poblacional por simplicidad y estabilidad en muestras pequeñas.
    /// </summary>
    public static double Stddev(IReadOnlyList<double> xs)
    {
        if (xs.Count <= 1) return 0;
        var m = Mean(xs);
        var variance = 0.0;
        foreach (var v in xs) variance += (v - m) * (v - m);
        variance /= xs.Count;
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Percentil por interpolación lineal (método R-7 / Excel). <paramref name="p"/> en [0, 1].
    /// </summary>
    public static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        if (xs.Count == 1) return xs[0];
        var sorted = xs.ToArray();
        Array.Sort(sorted);
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var weight = rank - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }
}
