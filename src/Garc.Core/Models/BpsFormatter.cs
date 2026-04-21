using System.Globalization;

namespace Garc.Core.Models;

/// <summary>
/// Formateo de bits/s a la unidad preferida del usuario. Devuelve
/// <c>(valor, sufijo)</c> para facilitar layouts con tipografía grande que
/// muestren ambos con estilos distintos.
/// </summary>
public readonly record struct BpsFormatter(ThroughputUnit Unit, CultureInfo? Culture = null)
{
    private CultureInfo Effective => Culture ?? CultureInfo.CurrentCulture;

    public (string Value, string Suffix) Format(ulong bps)
    {
        return Unit switch
        {
            ThroughputUnit.Mbps  => (FormatNumber(bps / 1_000_000.0),  "Mb/s"),
            ThroughputUnit.Mibps => (FormatNumber(bps / 1_048_576.0),  "Mib/s"),
            ThroughputUnit.MBps  => (FormatNumber(bps / 8_000_000.0),  "MB/s"),
            _ => throw new ArgumentOutOfRangeException(nameof(Unit)),
        };
    }

    public string FormatCompact(ulong bps)
    {
        var (v, s) = Format(bps);
        return $"{v} {s}";
    }

    private string FormatNumber(double v)
    {
        var culture = Effective;
        var maxFraction = v switch
        {
            >= 100 => 0,
            >= 10  => 1,
            _      => 2,
        };
        var fmt = maxFraction switch
        {
            0 => "0",
            1 => "0.#",
            _ => "0.##",
        };
        return v.ToString(fmt, culture);
    }
}
