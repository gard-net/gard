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

        return v4.FirstOrDefault(a => localPrefixes.Contains(Prefix24(a) ?? ""))
            ?? v4.FirstOrDefault(IsLikelyLan)
            ?? v4[0];
    }

    private static bool IsLikelyLan(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    private static string? Prefix24(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 ? $"{b[0]}.{b[1]}.{b[2]}" : null;
    }
}
