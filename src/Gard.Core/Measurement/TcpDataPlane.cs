using System.Net;
using System.Net.Sockets;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Measurement;

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

    /// <param name="expectedPeer">IP del peer de control. Conexiones de otra IP se
    /// cierran y se sigue esperando: sin esto un tercero que viera el puerto
    /// efímero podía ocupar el stream, recibir la bajada o inyectar bytes que se
    /// contaban como throughput de subida.</param>
    public async Task<IReadOnlyList<IFrameTransport>> AcceptHostAsync(
        HostAcceptance acceptance,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default,
        IPAddress? expectedPeer = null)
    {
        if (acceptance.Tag is not TcpListener[] listeners)
            throw LandspeedException.InternalInconsistency("HostAcceptance.Tag no contiene TcpListener[]");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var acceptTasks = new Task<Socket>[listeners.Length];
        try
        {
            for (var i = 0; i < listeners.Length; i++)
            {
                acceptTasks[i] = AcceptFromAsync(listeners[i], expectedPeer, timeoutCts);
            }
            var sockets = await Task.WhenAll(acceptTasks).ConfigureAwait(false);
            return sockets.Select(s => (IFrameTransport)TcpFrameTransport.FromAccepted(s)).ToArray();
        }
        catch (Exception ex)
        {
            // Antes se disponían `transports[i]` aún vacíos y los sockets ya
            // aceptados quedaban vivos dentro de las tasks: fuga de fds por
            // cada test fallido (watch los repite).
            DisposeCompleted(acceptTasks);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw LandspeedException.Timeout();
            throw;
        }
        finally
        {
            foreach (var l in listeners) { try { l.Stop(); } catch { } }
        }
    }

    private static async Task<Socket> AcceptFromAsync(TcpListener listener, IPAddress? expectedPeer, CancellationTokenSource cts)
    {
        try
        {
            while (true)
            {
                var socket = await listener.AcceptSocketAsync(cts.Token).ConfigureAwait(false);
                if (expectedPeer is null
                    || (socket.RemoteEndPoint is IPEndPoint ep && UdpDataPlane.SameHost(ep.Address, expectedPeer)))
                {
                    return socket;
                }
                socket.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Que los demás accepts no esperen hasta el timeout por un listener roto.
            cts.Cancel();
            throw;
        }
    }

    public async Task<IReadOnlyList<IFrameTransport>> ClientConnectAsync(
        string host,
        IReadOnlyList<int> ports,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        var tasks = new Task<TcpFrameTransport>[ports.Count];
        try
        {
            for (var i = 0; i < ports.Count; i++)
            {
                tasks[i] = TcpFrameTransport.ConnectAsync(host, ports[i], timeoutMs, cancellationToken);
            }
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Misma fuga que en el host: las conexiones exitosas vivían en las tasks.
            foreach (var t in tasks)
            {
                if (t is { IsCompletedSuccessfully: true })
                    try { await t.Result.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    private static void DisposeCompleted(Task<Socket>[] tasks)
    {
        foreach (var t in tasks)
        {
            if (t is { IsCompletedSuccessfully: true })
                try { t.Result.Dispose(); } catch { }
        }
    }
}
