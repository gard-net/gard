using System.Net;
using System.Net.Sockets;
using Gard.Core.Measurement;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Measurement;

[Collection(nameof(E2ETestCollection))]
public class TcpDataPlaneSecurityTests
{
    private static async Task<Socket> ConnectRawAsync(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, port);
        return s;
    }

    /// <summary>true si el otro extremo cerró (EOF o reset) dentro del plazo.</summary>
    private static async Task<bool> PeerClosedAsync(Socket s, int ms = 3_000)
    {
        var buf = new byte[16];
        using var cts = new CancellationTokenSource(ms);
        try
        {
            return await s.ReceiveAsync(buf, SocketFlags.None, cts.Token) == 0;
        }
        catch (SocketException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    [Fact(Timeout = 10_000)]
    public async Task AcceptHost_RejectsConnectionFromUnexpectedPeer()
    {
        var plane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var acceptance = await plane.HostOpenAsync(1);
        var acceptTask = plane.AcceptHostAsync(
            acceptance, timeoutMs: 600, expectedPeer: IPAddress.Parse("10.255.255.1"));

        using var rogue = await ConnectRawAsync(acceptance.Ports[0]);

        // El intruso es desconectado y el host sigue esperando a su peer real.
        Assert.True(await PeerClosedAsync(rogue));
        var ex = await Assert.ThrowsAsync<LandspeedException>(() => acceptTask);
        Assert.Equal(LandspeedErrorKind.Timeout, ex.Kind);
    }

    [Fact(Timeout = 10_000)]
    public async Task AcceptHost_AcceptsControlPeer_InDualStackForm()
    {
        var plane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var acceptance = await plane.HostOpenAsync(2);
        // El listener dual-stack ve ::ffff:127.0.0.1; el control dice 127.0.0.1.
        var acceptTask = plane.AcceptHostAsync(acceptance, timeoutMs: 5_000, expectedPeer: IPAddress.Loopback);

        using var a = await ConnectRawAsync(acceptance.Ports[0]);
        using var b = await ConnectRawAsync(acceptance.Ports[1]);
        var conns = await acceptTask;
        try
        {
            Assert.Equal(2, conns.Count);
        }
        finally
        {
            foreach (var c in conns) await c.DisposeAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task AcceptHost_PartialTimeout_ClosesAlreadyAcceptedSockets()
    {
        var plane = new TcpDataPlane { BindAddress = IPAddress.Loopback };
        var acceptance = await plane.HostOpenAsync(2);
        var acceptTask = plane.AcceptHostAsync(acceptance, timeoutMs: 500);

        // Sólo llega 1 de 2 streams.
        using var only = await ConnectRawAsync(acceptance.Ports[0]);
        await Assert.ThrowsAsync<LandspeedException>(() => acceptTask);

        // Antes el socket aceptado quedaba vivo dentro de la task (fuga de fd).
        Assert.True(await PeerClosedAsync(only), "el socket aceptado no se cerró");
    }

    [Fact(Timeout = 15_000)]
    public async Task ClientConnect_PartialFailure_ClosesSuccessfulConnections()
    {
        var good = new TcpListener(IPAddress.Loopback, 0);
        good.Start();
        var goodPort = ((IPEndPoint)good.LocalEndpoint).Port;
        var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop(); // puerto cerrado ⇒ connection refused

        var acceptTask = good.AcceptSocketAsync();
        var plane = new TcpDataPlane();
        await Assert.ThrowsAnyAsync<Exception>(
            () => plane.ClientConnectAsync("127.0.0.1", [goodPort, deadPort], timeoutMs: 3_000));

        using var serverSide = await acceptTask;
        good.Stop();
        Assert.True(await PeerClosedAsync(serverSide), "la conexión exitosa del cliente no se cerró");
    }
}
