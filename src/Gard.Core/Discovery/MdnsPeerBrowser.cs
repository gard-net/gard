using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Makaretu.Dns;

namespace Gard.Core.Discovery;

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

    // Fragmentos de records recibidos por instance name. Algunos advertisers
    // (p.ej. triuque en Windows) reparten SRV/TXT/A/AAAA en mensajes mDNS
    // distintos, así que mantenemos un buffer y emitimos cuando ya vimos
    // SRV + TXT. La llave interna es el nombre de instancia (case-insensitive).
    private readonly ConcurrentDictionary<string, InstanceBuffer> _buffers =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class InstanceBuffer
    {
        public SRVRecord? Srv;
        public TXTRecord? Txt;
        public readonly Dictionary<string, IPAddress> Addresses = new(StringComparer.OrdinalIgnoreCase);
    }

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
        // La respuesta al PTR en general incluye SRV+TXT+A/AAAA en
        // AdditionalRecords, pero no siempre: algunos advertisers los mandan
        // en mensajes separados. Lanzamos queries específicas al SD para
        // forzar resolución completa, y el buffer se irá llenando vía
        // AnswerReceived.
        _sd?.QueryServiceInstances(new DomainName(DiscoveryConstants.ServiceType));
        TryEmit(e.ServiceInstanceName.ToString(), e.Message);
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        // Cada peer activo reemite periódicamente; aprovechamos para ir
        // acumulando fragmentos (SRV/TXT/A/AAAA) en el buffer por instancia y
        // reevaluar los peers afectados.
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in e.Message.Answers.Concat(e.Message.AdditionalRecords))
        {
            switch (rec)
            {
                case PTRRecord ptr when IsLandspeedServiceType(ptr.Name):
                    touched.Add(ptr.DomainName.ToString());
                    break;
                case SRVRecord srv when IsLandspeedInstance(srv.Name):
                    var key = srv.Name.ToString();
                    Buffer(key).Srv = srv;
                    touched.Add(key);
                    break;
                case TXTRecord txt when IsLandspeedInstance(txt.Name):
                    var tkey = txt.Name.ToString();
                    Buffer(tkey).Txt = txt;
                    touched.Add(tkey);
                    break;
            }
        }

        // A/AAAA records vienen keyed por el SRV target, no por el service
        // instance, así que los asociamos vía el SRV de cada buffer conocido.
        foreach (var rec in e.Message.Answers.Concat(e.Message.AdditionalRecords))
        {
            if (rec is not (ARecord or AAAARecord)) continue;
            var addr = rec switch
            {
                ARecord a => a.Address,
                AAAARecord aaaa => aaaa.Address,
                _ => null,
            };
            if (addr is null) continue;
            foreach (var kv in _buffers)
            {
                var target = kv.Value.Srv?.Target.ToString();
                if (target is not null && NameEquals(rec.Name, target))
                {
                    kv.Value.Addresses[addr.ToString()] = addr;
                    touched.Add(kv.Key);
                }
            }
        }

        foreach (var name in touched) TryEmit(name, e.Message);
    }

    private InstanceBuffer Buffer(string instance) =>
        _buffers.GetOrAdd(instance, _ => new InstanceBuffer());

    private static bool IsLandspeedInstance(DomainName name)
    {
        // Un service instance termina con `._landspeed._tcp.local`.
        var s = name.ToString().TrimEnd('.');
        return s.EndsWith("." + DiscoveryConstants.ServiceType.TrimEnd('.').TrimStart('.'),
                          StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("." + DiscoveryConstants.ServiceType + "local",
                          StringComparison.OrdinalIgnoreCase)
            || s.Contains("._landspeed._tcp", StringComparison.OrdinalIgnoreCase);
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

    private void TryEmit(string serviceInstanceName, Message message)
    {
        // Primero actualizamos el buffer con cualquier fragmento que traiga
        // este mensaje puntual (útil para el caso ServiceInstanceDiscovered
        // clásico en el que todo viene junto).
        var buf = Buffer(serviceInstanceName);
        foreach (var rec in message.Answers.Concat(message.AdditionalRecords))
        {
            if (rec is SRVRecord s && NameEquals(s.Name, serviceInstanceName)) buf.Srv = s;
            if (rec is TXTRecord t && NameEquals(t.Name, serviceInstanceName)) buf.Txt = t;
        }
        if (buf.Srv is null || buf.Txt is null) return;

        var srv = buf.Srv;
        var txt = buf.Txt;

        var attrs = ParseTxt(txt);
        var parsed = LandspeedTxtRecord.Parse(attrs);
        if (parsed is null) return;
        if (parsed.ProtocolVersionMajor != DiscoveryConstants.ProtocolVersionMajor) return;

        var target = srv.Target.ToString();

        // A/AAAA pueden llegar en este mensaje (mismas señales) o haberse
        // acumulado en el buffer vía mensajes previos.
        foreach (var rec in message.Answers.Concat(message.AdditionalRecords))
        {
            if (rec is ARecord a && NameEquals(a.Name, target))    buf.Addresses[a.Address.ToString()] = a.Address;
            if (rec is AAAARecord q && NameEquals(q.Name, target)) buf.Addresses[q.Address.ToString()] = q.Address;
        }

        var addresses = buf.Addresses.Values.ToArray();
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
