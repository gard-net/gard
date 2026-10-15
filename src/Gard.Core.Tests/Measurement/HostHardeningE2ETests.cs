using System.Net;
using System.Net.Sockets;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Measurement;

/// <summary>
/// El host frente a un peer que manda parámetros hostiles o desaparece. El
/// "cliente" se maneja a mano para poder violar el flujo normal.
/// </summary>
[Collection(nameof(E2ETestCollection))]
public class HostHardeningE2ETests
{
    private sealed class Harness : IAsyncDisposable
    {
        public required TcpFrameTransport ClientCtl { get; init; }
        public required TcpFrameTransport HostCtl { get; init; }
        public required ControlMessageRouter ClientRouter { get; init; }
        public required ControlMessageRouter HostRouter { get; init; }

        public static async Task<Harness> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptSocketAsync();
            var client = await TcpFrameTransport.ConnectAsync("127.0.0.1", port);
            var host = TcpFrameTransport.FromAccepted(await acceptTask);
            listener.Stop();
            var h = new Harness
            {
                ClientCtl = client,
                HostCtl = host,
                ClientRouter = new ControlMessageRouter(new ControlStream(client)),
                HostRouter = new ControlMessageRouter(new ControlStream(host)),
            };
            h.ClientRouter.Start();
            h.HostRouter.Start();
            return h;
        }

        public Task<ResultBody> RunHost(Capabilities? caps, CancellationToken ct) =>
            MeasurementResponder.RunAsync(
                HostCtl, HostRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
                sessionId: "s1", tickIntervalMs: 200, negotiatedCaps: caps, cancellationToken: ct);

        public async ValueTask DisposeAsync()
        {
            await ClientRouter.DisposeAsync();
            await HostRouter.DisposeAsync();
            await ClientCtl.DisposeAsync();
            await HostCtl.DisposeAsync();
        }
    }

    private static TestStartBody Body(
        TestDirection direction = TestDirection.Down,
        double durationS = 0.5,
        int streams = 1,
        int payloadSize = 4096,
        TestTransport? transport = null) => new()
    {
        Direction = direction,
        DurationS = durationS,
        Streams = streams,
        PayloadSize = payloadSize,
        WarmupS = 0,
        Transport = transport,
    };

    [Fact(Timeout = 15_000)]
    public async Task OversizedTcpPayload_IsRejectedWith3004_BeforeAllocating()
    {
        await using var h = await Harness.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = h.RunHost(Capabilities.DefaultV1, cts.Token);

        await h.ClientCtl.SendControlAsync(
            new TestStartMessage(42, Body(payloadSize: int.MaxValue, streams: TestLimits.MaxStreams)), cts.Token);

        var clientEx = await Assert.ThrowsAsync<LandspeedException>(
            () => h.ClientRouter.AwaitTestStartAckAsync(5_000, cts.Token));
        Assert.Contains("code=3004", clientEx.Message);

        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.PayloadOutOfRange, hostEx.Kind);
    }

    [Fact(Timeout = 15_000)]
    public async Task UdpWithoutNegotiatedCap_IsRejectedWith3003()
    {
        await using var h = await Harness.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = h.RunHost(Capabilities.DefaultV1 & ~Capabilities.UdpDataPlane, cts.Token);

        await h.ClientCtl.SendControlAsync(
            new TestStartMessage(7, Body(transport: TestTransport.Udp, payloadSize: 1200)), cts.Token);

        var clientEx = await Assert.ThrowsAsync<LandspeedException>(
            () => h.ClientRouter.AwaitTestStartAckAsync(5_000, cts.Token));
        Assert.Contains("code=3003", clientEx.Message);

        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.UdpNotNegotiated, hostEx.Kind);
    }

    [Fact(Timeout = 15_000)]
    public async Task TooManyStreams_GetsNegativeAck()
    {
        await using var h = await Harness.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = h.RunHost(Capabilities.DefaultV1, cts.Token);

        await h.ClientCtl.SendControlAsync(new TestStartMessage(9, Body(streams: 50_000)), cts.Token);

        var ack = await h.ClientRouter.AwaitTestStartAckAsync(5_000, cts.Token);
        Assert.False(ack.Accepted);
        Assert.Empty(ack.DataPorts);
        Assert.Contains("streams", ack.Reason);

        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.TestRejected, hostEx.Kind);
    }

    [Fact(Timeout = 15_000)]
    public async Task PeerClosingBeforeTestStart_IsReportedAsCleanClose()
    {
        // Lo que hace `gard info`: handshake y cierre, sin test_start.
        await using var h = await Harness.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = h.RunHost(Capabilities.DefaultV1, cts.Token);

        await h.ClientCtl.DisposeAsync();

        var ex = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.PeerClosedWithoutTest, ex.Kind);
    }

    [Fact(Timeout = 15_000)]
    public async Task Router_Terminated_FiresWhenPeerCloses()
    {
        await using var h = await Harness.OpenAsync();
        var fired = new TaskCompletionSource();
        using var reg = h.HostRouter.Terminated.Register(() => fired.TrySetResult());

        await h.ClientCtl.DisposeAsync();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 30_000)]
    public async Task Host_StopsTcpTest_WhenClientDisconnectsMidPhase()
    {
        await using var h = await Harness.OpenAsync();
        // El token externo dura más que el test: lo que debe cortar es la desconexión.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var hostTask = h.RunHost(Capabilities.DefaultV1, cts.Token);

        await h.ClientCtl.SendControlAsync(new TestStartMessage(1, Body(durationS: 60)), cts.Token);
        var ack = await h.ClientRouter.AwaitTestStartAckAsync(5_000, cts.Token);
        Assert.True(ack.Accepted);

        var dataConns = await new TcpDataPlane { BindAddress = IPAddress.Loopback }
            .ClientConnectAsync("127.0.0.1", ack.DataPorts, timeoutMs: 5_000, cancellationToken: cts.Token);
        await Task.Delay(300);

        foreach (var c in dataConns) await c.DisposeAsync();
        await h.ClientCtl.DisposeAsync();

        var finished = await Task.WhenAny(hostTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(hostTask, finished);
        var ex = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.Transport, ex.Kind);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact(Timeout = 30_000)]
    public async Task UdpHost_CountsDataArrivingBeforeHello_InsteadOfCrashing()
    {
        await using var h = await Harness.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var hostTask = h.RunHost(Capabilities.DefaultV1, cts.Token);

        const int payload = 64;
        await h.ClientCtl.SendControlAsync(
            new TestStartMessage(3, Body(
                direction: TestDirection.Up, durationS: 0.3,
                payloadSize: payload, transport: TestTransport.Udp)), cts.Token);
        var ack = await h.ClientRouter.AwaitTestStartAckAsync(5_000, cts.Token);
        Assert.True(ack.Accepted);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect("127.0.0.1", ack.DataPorts[0]);

        // Dato (seq=1) ANTES que el HELLO (seq=0): HELLO reordenado/perdido.
        // Antes esto disparaba una NullReferenceException en el host.
        var data = new byte[payload];
        UdpDataPlane.WriteHeader(data, seq: 1, tsNs: 1, stream: 0);
        await udp.SendAsync(data, cts.Token);
        var hello = new byte[UdpDataPlane.HeaderSize];
        UdpDataPlane.WriteHeader(hello, seq: 0, tsNs: 2, stream: 0);
        await udp.SendAsync(hello, cts.Token);

        await Task.Delay(200, cts.Token);
        await h.ClientCtl.SendControlAsync(new TestEndMessage(4, new TestEndBody()), cts.Token);
        await h.ClientCtl.SendControlAsync(
            new UdpStatsReportMessage(5, new UdpStatsReportBody
            {
                PerStream = [new UdpStatsPerStream { Stream = 0, PacketsSent = 1, BytesSent = payload }],
            }), cts.Token);

        var hostReport = await h.ClientRouter.AwaitUdpStatsReportAsync(10_000, cts.Token);
        Assert.Equal(1UL, hostReport.PerStream[0].PacketsReceived);

        var (_, result) = await h.ClientRouter.AwaitResultAsync(10_000, cts.Token);
        Assert.NotNull(result.Udp);
        await hostTask;
    }
}
