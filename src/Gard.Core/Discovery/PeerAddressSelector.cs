using System.Net;
using System.Net.Sockets;

namespace Gard.Core.Discovery;

public static class PeerAddressSelector
{
    public static IPAddress? PreferUsefulAddress(IReadOnlyCollection<IPAddress> addresses)
        => PreferUsefulAddress(addresses, Dns.GetHostAddresses(Dns.GetHostName()));

    public static IPAddress? PreferUsefulAddress(
        IReadOnlyCollection<IPAddress> addresses,
        IReadOnlyCollection<IPAddress> localAddresses)
    {
        var v4 = addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Where(a => !IPAddress.IsLoopback(a))
            .Where(a => !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .ToArray();
        if (v4.Length == 0) return null;

        var localPrefixes = localAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(Prefix24)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.Ordinal);

        // Orden: no-CGNAT antes que CGNAT (Tailscale/carrier: 100.64/10 suele estar
        // en ambos equipos y "ganaba" a la LAN real), misma /24 antes que otra, y
        // entre privadas 192.168 > 10 > 172.16/12 (puentes Docker).
        return v4
            .OrderBy(a => IsCgnat(a))
            .ThenBy(a => !localPrefixes.Contains(Prefix24(a) ?? ""))
            .ThenBy(Kind)
            .First();
    }

    private static bool IsCgnat(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static int Kind(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b[0] == 192 && b[1] == 168) return 0;
        if (b[0] == 10) return 1;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 2;
        return IsCgnat(address) ? 4 : 3;
    }

    private static string? Prefix24(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 ? $"{b[0]}.{b[1]}.{b[2]}" : null;
    }
}
