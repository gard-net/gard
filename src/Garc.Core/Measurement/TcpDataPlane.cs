using System.Net;
using System.Net.Sockets;
using Garc.Core.Protocol;
using Garc.Core.Transport;

namespace Garc.Core.Measurement;

/// <summary>
/// Implementación concreta de <see cref="IDataPlane"/> sobre TCP. Abre N
/// listeners en puertos efímeros, acepta exactamente una conexión por listener
/// y los cierra inmediatamente después. En el lado cliente, conecta N sockets
/// a los puertos anunciados por el host.
/// </summary>
public sealed class TcpDataPlane : IDataPlane
{
    /// <summary>
    /// Dirección a la que atarse al abrir los listeners del host. Por defecto
    /// <see cref="IPAddress.Any"/> (todas las interfaces IPv4). Windows la UI
    /// puede pasar una IP específica para forzar bind a una NIC concreta.
    /// </summary>
    public IPAddress BindAddress { get; init; } = IPAddress.IPv6Any;

    public async Task<HostAcceptance> HostOpenAsync(int count, CancellationToken cancellationToken = default)
    {
        var listeners = new TcpListener[count];
        var ports = new int[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var listener = new TcpListener(BindAddress, 0);
                if (BindAddress.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    listener.Server.DualMode = true;
                }
                listener.Start();
                listeners[i] = listener;
                ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
        catch
        {
            foreach (var l in listeners) { try { l?.Stop(); } catch { } }
            throw;
        }

        return await Task.FromResult(new HostAcceptance
        {
            Ports = ports,
            Tag = listeners,
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IFrameTransport>> AcceptHostAsync(
        HostAcceptance acceptance,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (acceptance.Tag is not TcpListener[] listeners)
            throw LandspeedException.InternalInconsistency("HostAcceptance.Tag no contiene TcpListener[]");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var transports = new IFrameTransport[listeners.Length];
        try
        {
            var acceptTasks = new Task<Socket>[listeners.Length];
            for (var i = 0; i < listeners.Length; i++)
            {
                acceptTasks[i] = listeners[i].AcceptSocketAsync(timeoutCts.Token).AsTask();
            }
            var sockets = await Task.WhenAll(acceptTasks).ConfigureAwait(false);
            for (var i = 0; i < sockets.Length; i++)
            {
                transports[i] = TcpFrameTransport.FromAccepted(sockets[i]);
            }
            return transports;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var t in transports)
            {
                if (t is not null) try { await t.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw LandspeedException.Timeout();
        }
        catch
        {
            foreach (var t in transports)
            {
                if (t is not null) try { await t.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
        finally
        {
            foreach (var l in listeners) { try { l.Stop(); } catch { } }
        }
    }

    public async Task<IReadOnlyList<IFrameTransport>> ClientConnectAsync(
        string host,
        IReadOnlyList<int> ports,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        var transports = new IFrameTransport[ports.Count];
        try
        {
            var tasks = new Task<TcpFrameTransport>[ports.Count];
            for (var i = 0; i < ports.Count; i++)
            {
                tasks[i] = TcpFrameTransport.ConnectAsync(host, ports[i], timeoutMs, cancellationToken);
            }
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < results.Length; i++) transports[i] = results[i];
            return transports;
        }
        catch
        {
            foreach (var t in transports)
            {
                if (t is not null) try { await t.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }
}
