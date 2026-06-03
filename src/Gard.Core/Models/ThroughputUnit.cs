using System.Text.Json.Serialization;

namespace Gard.Core.Models;

/// <summary>
/// Unidad de throughput mostrada en UI. El wire SIEMPRE es bits/s (spec §3.2);
/// esto sólo afecta presentación.
/// </summary>
public enum ThroughputUnit
{
    /// <summary>Megabits por segundo (10^6 bits/s).</summary>
    [JsonStringEnumMemberName("mbps")] Mbps,
    /// <summary>Mebibits por segundo (2^20 bits/s).</summary>
    [JsonStringEnumMemberName("mibps")] Mibps,
    /// <summary>Megabytes por segundo (10^6 bytes/s).</summary>
    [JsonStringEnumMemberName("mbps_bytes")] MBps,
}
