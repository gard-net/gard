using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Makaretu.Dns;

namespace Garc.Core.Discovery;

/// <summary>
/// Evento emitido por <see cref="MdnsPeerBrowser"/>.
/// </summary>
public sealed record PeerEvent
{
    public enum EventKind { Added, Updated, Removed }

    public required EventKind Kind { get; init; }
    public required string ServiceInstanceName { get; init; }
    /// <summary>Poblado para <see cref="EventKind.Added"/> y <see cref="EventKind.Updated"/>; <c>null</c> para <see cref="EventKind.Removed"/>.</summary>
    public DiscoveredPeer? Peer { get; init; }
}

/// <summary>
/// Browser mDNS/DNS-SD para peers <c>_landspeed._tcp</c>. Escucha anuncios en
/// la LAN, resuelve SRV/TXT/A/AAAA y expone eventos mediante un
/// <see cref="Channel{T}"/> — el consumidor típico en UI es
/// <c>await foreach (var e in browser.Events.ReadAllAsync())</c>.
///
/// No realiza auto-connect: simplemente mantiene la tabla de peers vivos y
/// propaga altas/bajas/actualizaciones.
/// </summary>
public sealed class MdnsPeerBrowser : IAsyncDisposable
{
    private readonly Channel<PeerEvent> _events =
        Channel.CreateUnbounded<PeerEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private int _disposed;

    public ChannelReader<PeerEvent> Events => _events.Reader;

    /// <summary>Snapshot de peers actualmente conocidos (no bloqueante).</summary>
    public IReadOnlyCollection<DiscoveredPeer> CurrentPeers => _peers.Values.ToArray();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_mdns is not null) return Task.CompletedTask;

        var mdns = new MulticastService();
        var sd = new ServiceDiscovery(mdns);
        _mdns = mdns;
        _sd = sd;

        sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        sd.ServiceInstanceShutdown += OnServiceInstanceShutdown;
        // Las respuestas (SRV/TXT/A/AAAA) llegan como Message.Answers en el
        // evento AnswerReceived del MulticastService.
        mdns.AnswerReceived += OnAnswerReceived;

        mdns.Start();
        sd.QueryServiceInstances(new DomainName(DiscoveryConstants.ServiceType));
        return Task.CompletedTask;
    }

    /// <summary>Reemite la consulta PTR — útil para forzar refresh en UI.</summary>
    public void Refresh()
    {
        _sd?.QueryServiceInstances(new DomainName(DiscoveryConstants.ServiceType));
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        // La respuesta al PTR suele venir acompañada de SRV+TXT+A/AAAA en
        // AdditionalRecords; procesamos todos los records del mensaje.
        TryIngest(e.ServiceInstanceName.ToString(), e.Message);
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        // Cada peer activo reemite periódicamente; aprovechamos para actualizar
        // direcciones si aparecieron A/AAAA en respuestas posteriores.
        foreach (var rec in e.Message.Answers.Concat(e.Message.AdditionalRecords))
        {
            if (rec is PTRRecord ptr && IsLandspeedServiceType(ptr.Name))
            {
                TryIngest(ptr.DomainName.ToString(), e.Message);
            }
        }
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var key = e.ServiceInstanceName.ToString();
        if (_peers.TryRemove(key, out _))
        {
            _events.Writer.TryWrite(new PeerEvent
            {
                Kind = PeerEvent.EventKind.Removed,
                ServiceInstanceName = key,
            });
        }
    }

    private static bool IsLandspeedServiceType(DomainName name)
    {
        var s = name.ToString();
        return s.StartsWith(DiscoveryConstants.ServiceType, StringComparison.OrdinalIgnoreCase);
    }

    private void TryIngest(string serviceInstanceName, Message message)
    {
        var srv = message.Answers.OfType<SRVRecord>()
            .Concat(message.AdditionalRecords.OfType<SRVRecord>())
            .FirstOrDefault(r => NameEquals(r.Name, serviceInstanceName));

        var txt = message.Answers.OfType<TXTRecord>()
            .Concat(message.AdditionalRecords.OfType<TXTRecord>())
            .FirstOrDefault(r => NameEquals(r.Name, serviceInstanceName));

        if (srv is null || txt is null) return;

        var attrs = ParseTxt(txt);
        var parsed = LandspeedTxtRecord.Parse(attrs);
        if (parsed is null) return;
        if (parsed.ProtocolVersionMajor != DiscoveryConstants.ProtocolVersionMajor) return;

        var target = srv.Target.ToString();
        var addresses = message.Answers.Concat(message.AdditionalRecords)
            .Where(r => NameEquals(r.Name, target))
            .Select(r => r switch
            {
                ARecord a => a.Address,
                AAAARecord aaaa => aaaa.Address,
                _ => null,
            })
            .OfType<IPAddress>()
            .ToArray();

        var host = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString()
                ?? addresses.FirstOrDefault()?.ToString()
                ?? target;

        var peer = new DiscoveredPeer
        {
            ServiceName = serviceInstanceName,
            DeviceName = parsed.Name,
            Platform = parsed.Platform,
            Caps = parsed.Caps,
            ProtocolVersionMajor = parsed.ProtocolVersionMajor,
            Host = host,
            Port = srv.Port,
        };

        PeerEvent.EventKind kind;
        if (_peers.TryGetValue(serviceInstanceName, out var prev))
        {
            if (prev.Equals(peer)) return; // sin cambios: no emitimos ruido.
            kind = PeerEvent.EventKind.Updated;
        }
        else
        {
            kind = PeerEvent.EventKind.Added;
        }
        _peers[serviceInstanceName] = peer;

        _events.Writer.TryWrite(new PeerEvent
        {
            Kind = kind,
            ServiceInstanceName = serviceInstanceName,
            Peer = peer,
        });
    }

    private static bool NameEquals(DomainName name, string other) =>
        string.Equals(name.ToString().TrimEnd('.'), other.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> ParseTxt(TXTRecord txt)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in txt.Strings)
        {
            var eq = s.IndexOf('=');
            if (eq <= 0) continue;
            var key = s[..eq];
            var val = s[(eq + 1)..];
            result[key] = val;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_sd is not null)
        {
            _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
            _sd.ServiceInstanceShutdown -= OnServiceInstanceShutdown;
        }
        if (_mdns is not null)
        {
            _mdns.AnswerReceived -= OnAnswerReceived;
        }
        try { _sd?.Dispose(); } catch { }
        try { _mdns?.Stop(); } catch { }
        if (_mdns is IDisposable d) { try { d.Dispose(); } catch { } }
        _events.Writer.TryComplete();
        _sd = null; _mdns = null;
        await Task.CompletedTask;
    }
}
