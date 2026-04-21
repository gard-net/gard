using System.Diagnostics;

namespace Garc.Core.Utils;

/// <summary>
/// Reloj monotónico en nanosegundos. Usado para ping, clock_sync y RTT bajo
/// carga. Basado en <see cref="Stopwatch.GetTimestamp"/> — NO usar
/// <c>DateTime.UtcNow</c> (resolución ~16 ms en Windows, no monotónico).
/// </summary>
public static class MonotonicClock
{
    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    public static ulong NowNs()
    {
        var ts = Stopwatch.GetTimestamp();
        // ts es signed long pero siempre positivo desde el arranque del proceso.
        return (ulong)(ts * TicksToNs);
    }
}
