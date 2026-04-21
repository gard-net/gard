namespace Garc.Core.Models;

/// <summary>
/// Unidad de throughput mostrada en UI. El wire SIEMPRE es bits/s (spec §3.2);
/// esto sólo afecta presentación.
/// </summary>
public enum ThroughputUnit
{
    /// <summary>Megabits por segundo (10^6 bits/s).</summary>
    Mbps,
    /// <summary>Mebibits por segundo (2^20 bits/s).</summary>
    Mibps,
    /// <summary>Megabytes por segundo (10^6 bytes/s).</summary>
    MBps,
}
