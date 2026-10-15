using System.Net;
using System.Net.Sockets;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Measurement;

/// <summary>Conformidad del data plane UDP con LSP §12 y sus defensas.</summary>
[Collection(nameof(E2ETestCollection))]
public class UdpConformanceTests
{
    private const int W = UdpReceiverStats.DuplicateWindow;

    private static void Feed(UdpReceiverStats s, ulong seq) => s.OnPacket(seq, 0, 0, 100);

    // ── Tracker de seqs acotado ────────────────────────────────────────────

    [Fact]
    public void Tracker_DetectsDuplicatesAndReorderWithinWindow()
    {
        var s = new UdpReceiverStats();
        foreach (var seq in new ulong[] { 1, 2, 4, 3, 4, 2, 5 }) Feed(s, seq);

        Assert.Equal(5UL, s.PacketsReceived);
        Assert.Equal(2UL, s.DuplicateCount);
        Assert.Equal(1UL, s.ReorderCount); // el 3 llegó después del 4
        Assert.Equal(5UL, s.MaxSeqSeen);
    }

    [Fact]
    public void Tracker_ClearsReusedSlots_WhenWindowAdvances()
    {
        var s = new UdpReceiverStats();
        for (ulong seq = 1; seq <= W; seq++) Feed(s, seq);
        // Saltar 3: W+1 y W+2 reutilizan los slots de 1 y 2 y deben quedar libres.
        Feed(s, W + 3);
        Feed(s, W + 1);
        Feed(s, W + 2);

        Assert.Equal(0UL, s.DuplicateCount);
        Assert.Equal((ulong)W + 3, s.PacketsReceived);
        Feed(s, W + 1);
        Assert.Equal(1UL, s.DuplicateCount);
    }

    [Fact]
    public void Tracker_PacketOlderThanWindow_CountsAsLateReorder()
    {
        var s = new UdpReceiverStats();
        Feed(s, 10);
        Feed(s, 10 + W + 5);
        Feed(s, 10); // fuera de la ventana: no verificable como duplicado

        Assert.Equal(3UL, s.PacketsReceived);
        Assert.Equal(0UL, s.DuplicateCount);
        Assert.Equal(1UL, s.ReorderCount);
    }

    [Fact]
    public void Tracker_HugeSeqJumps_StayCheap()
    {
        // Un emisor hostil con seqs gigantes o saltos de casi una ventana no
        // debe costar O(ventana) por paquete ni crecer en memoria.
        var s = new UdpReceiverStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong seq = 1;
        for (var i = 0; i < 200_000; i++)
        {
            seq += W - 1;
            Feed(s, seq);
        }
        Feed(s, ulong.MaxValue - 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"tardó {sw.Elapsed}");
        Assert.Equal(200_001UL, s.PacketsReceived);
    }

    // ── §12.6 roles por stream ─────────────────────────────────────────────

    [Theory]
    [InlineData(TestDirection.Down, 0, true, false)]
    [InlineData(TestDirection.Down, 3, true, false)]
    [InlineData(TestDirection.Up, 0, false, true)]
    [InlineData(TestDirection.Up, 3, false, true)]
    [InlineData(TestDirection.Bidir, 0, true, false)]
    [InlineData(TestDirection.Bidir, 1, false, true)]
    [InlineData(TestDirection.Bidir, 2, true, false)]
    [InlineData(TestDirection.Bidir, 3, false, true)]
    public void StreamRoles_FollowSpec(TestDirection dir, int stream, bool down, bool up)
    {
        Assert.Equal(down, UdpMeasurement.StreamCarriesDown(dir, stream));
        Assert.Equal(up, UdpMeasurement.StreamCarriesUp(dir, stream));
    }

    [Fact]
    public void Validator_RejectsSingleStreamUdpBidir()
    {
        var body = new TestStartBody
        {
            Direction = TestDirection.Bidir, DurationS = 1, Streams = 1, PayloadSize = 1200,
            WarmupS = 0, Transport = TestTransport.Udp,
        };
        Assert.Equal(LandspeedErrorKind.TestRejected, TestStartValidator.Validate(body, Capabilities.DefaultV1)?.Kind);
        Assert.Null(TestStartValidator.Validate(body with { Streams = 2 }, Capabilities.DefaultV1));
    }

    // ── HELLO y origen de datagramas ───────────────────────────────────────

    [Fact(Timeout = 10_000)]
    public async Task HostDrain_IgnoresHelloFromUnexpectedPeer()
    {
        await using var host = UdpDataPlane.HostOpen(1, IPAddress.IPv6Any);
        await using var client = await UdpDataPlane.ClientConnectAsync("127.0.0.1", host.Ports, 1);

        // El HELLO llega desde 127.0.0.1, pero el peer de control es otra IP.
        await Assert.ThrowsAsync<TimeoutException>(() => UdpDataPlane.HostDrainHelloAsync(
            host, timeoutMs: 400, onStrayData: null, expectedPeer: IPAddress.Parse("10.255.255.1")));
    }

    [Fact(Timeout = 10_000)]
    public async Task HostDrain_AcceptsHelloFromControlPeer_DualStackForm()
    {
        await using var host = UdpDataPlane.HostOpen(1, IPAddress.IPv6Any);
        await using var client = await UdpDataPlane.ClientConnectAsync("127.0.0.1", host.Ports, 1);

        // El socket dual-stack ve ::ffff:127.0.0.1; el control puede decir 127.0.0.1.
        var eps = await UdpDataPlane.HostDrainHelloAsync(
            host, timeoutMs: 3_000, onStrayData: null, expectedPeer: IPAddress.Loopback);
        Assert.True(UdpDataPlane.SameHost(eps[0].Address, IPAddress.Loopback));
    }

    [Fact(Timeout = 10_000)]
    public async Task Receiver_DropsDatagramsFromOtherSources()
    {
        await using var host = UdpDataPlane.HostOpen(1, IPAddress.Loopback);
        await using var legit = await UdpDataPlane.ClientConnectAsync("127.0.0.1", host.Ports, 1);
        await using var rogue = await UdpDataPlane.ClientConnectAsync("127.0.0.1", host.Ports, 1);
        var eps = await UdpDataPlane.HostDrainHelloAsync(host, 3_000, null);

        var stats = new UdpReceiverStats();
        using var cts = new CancellationTokenSource();
        var recv = UdpReceiver.RunAsync(host.Sockets[0], stats, cts.Token, expectedSource: eps[0]);

        var pkt = new byte[64];
        for (ulong seq = 1; seq <= 5; seq++)
        {
            UdpDataPlane.WriteHeader(pkt, seq, 0, 0);
            legit.Sockets[0].Client.Send(pkt);
            UdpDataPlane.WriteHeader(pkt, seq + 100, 0, 0);
            rogue.Sockets[0].Client.Send(pkt);
        }
        await Task.Delay(300);
        cts.Cancel();
        await recv;

        // El primer HELLO aprendido puede ser de cualquiera de los dos; sólo
        // deben contarse los 5 del origen aprendido.
        Assert.Equal(5UL, stats.PacketsReceived);
    }

    [Fact(Timeout = 10_000)]
    public async Task LostFirstHello_IsRecoveredByRetransmit()
    {
        await using var host = UdpDataPlane.HostOpen(1, IPAddress.Loopback);
        await using var client = await UdpDataPlane.ClientConnectAsync("127.0.0.1", host.Ports, 1);

        // Simular la pérdida: consumir el HELLO inicial antes de que el host drene.
        using (var eat = new CancellationTokenSource(2_000))
            _ = await host.Sockets[0].ReceiveAsync(eat.Token);

        using var cts = new CancellationTokenSource();
        var retransmit = UdpDataPlane.RetransmitHelloAsync(client, cts.Token);
        var eps = await UdpDataPlane.HostDrainHelloAsync(host, timeoutMs: 3_000, onStrayData: null);
        cts.Cancel();
        await retransmit;

        Assert.NotNull(eps[0]);
    }

    [Fact(Timeout = 10_000)]
    public async Task Client_SupportsIpv6Peer()
    {
        if (!Socket.OSSupportsIPv6) return;
        await using var host = UdpDataPlane.HostOpen(1, IPAddress.IPv6Loopback);
        await using var client = await UdpDataPlane.ClientConnectAsync(
            "::1", host.Ports, 1, remoteAddress: IPAddress.IPv6Loopback);

        var eps = await UdpDataPlane.HostDrainHelloAsync(host, 3_000, null, expectedPeer: IPAddress.IPv6Loopback);
        Assert.Equal(AddressFamily.InterNetworkV6, eps[0].AddressFamily);
    }

    // ── End-to-end bidir ───────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task BidirUdp_EvenStreamsDown_OddStreamsUp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        await using var clientCtl = await TcpFrameTransport.ConnectAsync("127.0.0.1", port);
        await using var hostCtl = TcpFrameTransport.FromAccepted(await acceptTask);
        listener.Stop();
        await using var clientRouter = new ControlMessageRouter(new ControlStream(clientCtl));
        await using var hostRouter = new ControlMessageRouter(new ControlStream(hostCtl));
        clientRouter.Start();
        hostRouter.Start();

        var parms = new TestParameters
        {
            Direction = TestDirection.Bidir,
            BidirMode = BidirMode.Simultaneous,
            DurationS = 1.0,
            Streams = 4,
            PayloadSize = 1200,
            WarmupS = 0,
            Transport = TestTransport.Udp,
            TargetBitrateBps = 10_000_000,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hostTask = MeasurementResponder.RunAsync(
            hostCtl, hostRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
            sessionId: "bidir", negotiatedCaps: Capabilities.DefaultV1, cancellationToken: cts.Token);
        var clientTask = MeasurementSession.RunClientAsync(
            clientCtl, clientRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
            remoteHost: "127.0.0.1",
            inputs: new MeasurementClientInputs
            {
                Parameters = parms,
                PeerName = "host",
                PeerPlatform = PeerPlatform.Linux,
                SessionId = "bidir",
                NegotiatedCaps = Capabilities.DefaultV1,
            },
            onProgress: null,
            cancellationToken: cts.Token);

        await Task.WhenAll(hostTask, clientTask);
        var result = await clientTask;

        Assert.NotNull(result.ThroughputUp);
        Assert.NotNull(result.ThroughputDown);
        // 2 streams por sentido a 10 Mb/s c/u ⇒ ~20 Mb/s por sentido, no 40.
        Assert.InRange((double)result.ThroughputUp!.MeanBps, 12e6, 24e6);
        Assert.InRange((double)result.ThroughputDown!.MeanBps, 12e6, 24e6);

        var up = result.ThroughputUp.PerStreamBps;
        var down = result.ThroughputDown.PerStreamBps;
        Assert.Equal(0UL, up[0]);   // stream 0 sólo baja
        Assert.True(up[1] > 0);
        Assert.True(down[0] > 0);
        Assert.Equal(0UL, down[1]); // stream 1 sólo sube
        Assert.All(result.PerStreamBps, bps => Assert.True(bps > 0));
    }
}
