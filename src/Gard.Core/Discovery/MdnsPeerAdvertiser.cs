using System.Net;
using Makaretu.Dns;

namespace Gard.Core.Discovery;

/// <summary>
/// Anuncia este dispositivo como servicio <c>_landspeed._tcp</c> en la LAN vía
/// mDNS/DNS-SD. Wrappea <see cref="ServiceDiscovery"/> de Makaretu: creamos un
/// <see cref="ServiceProfile"/> con los TXT del dispositivo y lo publicamos;
/// al disponer, se envía el <c>goodbye</c> automáticamente.
/// </summary>
public sealed class MdnsPeerAdvertiser : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly int _port;
    private readonly string _instanceName;
    private readonly IEnumerable<IPAddress>? _addresses;

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private int _disposed;

    /// <summary>
    /// Crea un advertiser. Por defecto el nombre de instancia es
    /// <see cref="DeviceIdentity.Name"/>; se sobrescribe con
    /// <paramref name="instanceName"/> si se necesita forzar algo único.
    /// Si <paramref name="addresses"/> es <c>null</c>, se anuncian todas las
    /// IPs locales detectadas por Makaretu.
    /// </summary>
    public MdnsPeerAdvertiser(
        DeviceIdentity identity,
        int port = DiscoveryConstants.DefaultPort,
        string? instanceName = null,
        IEnumerable<IPAddress>? addresses = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _port = port;
        _instanceName = SanitizeInstanceName(instanceName ?? identity.Name);
        _addresses = addresses;
    }

    /// <summary>Nombre de instancia efectivamente usado (ya saneado para DNS).</summary>
    public string InstanceName => _instanceName;

    /// <summary>Fully-qualified name del servicio anunciado, p.ej. <c>"MyMac._landspeed._tcp.local"</c>.</summary>
    public string FullyQualifiedName =>
        $"{_instanceName}.{DiscoveryConstants.ServiceType}.local";

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_mdns is not null) return Task.CompletedTask;

        var profile = new ServiceProfile(
            instanceName: new DomainName(_instanceName),
            serviceName: new DomainName(DiscoveryConstants.ServiceType),
            port: checked((ushort)_port),
            addresses: _addresses);

        var txt = new LandspeedTxtRecord
        {
            ProtocolVersionMajor = DiscoveryConstants.ProtocolVersionMajor,
            Name = _identity.Name,
            Platform = _identity.Platform,
            Caps = _identity.Caps,
        }.ToAttributes();
        foreach (var kv in txt)
        {
            profile.AddProperty(kv.Key, kv.Value);
        }

        var mdns = new MulticastService();
        var sd = new ServiceDiscovery(mdns);

        _profile = profile;
        _mdns = mdns;
        _sd = sd;

        sd.Advertise(profile);
        mdns.Start();
        // Refuerzo: anunciar varias veces seguidas para que peers recientes
        // no pierdan el primer paquete si acaban de levantar su browser.
        sd.Announce(profile);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { if (_profile is not null) _sd?.Unadvertise(_profile); } catch { }
        try { _sd?.Dispose(); } catch { }
        try { _mdns?.Stop(); } catch { }
        if (_mdns is IDisposable d) { try { d.Dispose(); } catch { } }
        _sd = null; _mdns = null; _profile = null;
        await Task.CompletedTask;
    }

    private static string SanitizeInstanceName(string name)
    {
        // DNS-SD permite UTF-8 en el label; mDNS limita el label a 63 bytes y
        // no acepta '.' sin escapar. Reemplazamos puntos por guiones y cortamos.
        var cleaned = name.Replace('.', '-');
        while (System.Text.Encoding.UTF8.GetByteCount(cleaned) > 63 && cleaned.Length > 0)
        {
            cleaned = cleaned[..^1];
        }
        return string.IsNullOrWhiteSpace(cleaned) ? "landspeed" : cleaned;
    }
}
