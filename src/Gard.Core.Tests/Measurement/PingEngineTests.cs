using System.Net;
using System.Net.Sockets;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Measurement;

[Collection(nameof(E2ETestCollection))]
public class PingEngineTests
{
    [Fact(Timeout = 15_000)]
    public async Task LossProbe_KeepsSamples_WhenCancelledWithPongPending()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        await using var client = await TcpFrameTransport.ConnectAsync("127.0.0.1", port);
        await using var host = TcpFrameTransport.FromAccepted(await acceptTask);
        listener.Stop();
        await using var clientRouter = new ControlMessageRouter(new ControlStream(client));
        await using var hostRouter = new ControlMessageRouter(new ControlStream(host));
        clientRouter.Start();
        hostRouter.Start();

        using var responderCts = new CancellationTokenSource();
        var responder = PingResponder.RunAsync(host, hostRouter, responderCts.Token);

        using var probeCts = new CancellationTokenSource();
        var probe = PingEngine.RunLossProbeAsync(
            client, clientRouter, intervalMs: 20, timeoutMs: 5_000, cancellationToken: probeCts.Token);

        await Task.Delay(400);
        // El host deja de responder: el próximo ping queda esperando su pong.
        responderCts.Cancel();
        await responder;
        await Task.Delay(150);
        probeCts.Cancel();

        // Antes: la cancelación con un pong pendiente hacía fallar el task y el
        // cliente reemplazaba el resultado por un PingStats vacío.
        var stats = await probe;
        Assert.True(stats.Samples.Count >= 3, $"samples={stats.Samples.Count}");
        Assert.Equal(0, stats.LossPct);
    }
}
