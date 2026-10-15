using System.Net;
using System.Net.Sockets;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Measurement;

[Collection(nameof(E2ETestCollection))]
public class TcpDataPlaneE2ETests
{
    private static async Task<(TcpFrameTransport client, TcpFrameTransport server)> OpenControlPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var client = await TcpFrameTransport.ConnectAsync("127.0.0.1", port);
        var server = TcpFrameTransport.FromAccepted(await acceptTask);
        listener.Stop();
        return (client, server);
    }

    [Fact]
    public async Task HostOpenAndClientConnect_MatchPorts()
    {
        var hostPlane = new TcpDataPlane { BindAddress = IPAddress.Loopback };
        var clientPlane = new TcpDataPlane { BindAddress = IPAddress.Loopback };

        var acceptance = await hostPlane.HostOpenAsync(3);
        Assert.Equal(3, acceptance.Ports.Count);
        foreach (var p in acceptance.Ports) Assert.True(p > 0);

        var acceptTask = hostPlane.AcceptHostAsync(acceptance, timeoutMs: 5_000);
        var connectTask = clientPlane.ClientConnectAsync("127.0.0.1", acceptance.Ports, timeoutMs: 5_000);

        var hostConns = await acceptTask;
        var clientConns = await connectTask;

        try
        {
            Assert.Equal(3, hostConns.Count);
            Assert.Equal(3, clientConns.Count);
        }
        finally
        {
            foreach (var c in clientConns) await c.DisposeAsync();
            foreach (var c in hostConns) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptHost_TimesOutIfNoClient()
    {
        var plane = new TcpDataPlane { BindAddress = IPAddress.Loopback };
        var acceptance = await plane.HostOpenAsync(1);
        var ex = await Record.ExceptionAsync(async () =>
        {
            await plane.AcceptHostAsync(acceptance, timeoutMs: 200);
        });
        Assert.NotNull(ex);
    }

    [Fact(Timeout = 15_000)]
    public async Task LoopbackDownTest_Completes()
    {
        var (clientCtl, hostCtl) = await OpenControlPairAsync();
        await using var clientCtlAsync = clientCtl;
        await using var hostCtlAsync = hostCtl;
        await using var clientRouter = new ControlMessageRouter(new ControlStream(clientCtl));
        await using var hostRouter = new ControlMessageRouter(new ControlStream(hostCtl));
        clientRouter.Start();
        hostRouter.Start();

        var clientPlane = new TcpDataPlane { BindAddress = IPAddress.Loopback };
        var hostPlane = new TcpDataPlane { BindAddress = IPAddress.Loopback };

        var parms = new TestParameters
        {
            Direction = TestDirection.Down,
            DurationS = 0.5,
            Streams = 2,
            PayloadSize = 4096,
            WarmupS = 0.0,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = MeasurementResponder.RunAsync(
            hostCtl, hostRouter, hostPlane,
            sessionId: "s1", tickIntervalMs: 200,
            cancellationToken: cts.Token);

        var inputs = new MeasurementClientInputs
        {
            Parameters = parms,
            PeerName = "host",
            PeerPlatform = PeerPlatform.Macos,
            SessionId = "s1",
            NegotiatedCaps = Capabilities.ParallelStreams | Capabilities.IntervalReporting,
        };
        var clientTask = MeasurementSession.RunClientAsync(
            clientCtl, clientRouter, clientPlane,
            remoteHost: "127.0.0.1",
            inputs: inputs,
            onProgress: null,
            cancellationToken: cts.Token);

        await Task.WhenAll(hostTask, clientTask);
        var result = await clientTask;

        Assert.Equal("s1", result.SessionId);
        Assert.Equal(TestDirection.Down, result.Direction);
        Assert.Equal(2, result.Streams);
        Assert.True(result.MeanBps > 0, $"MeanBps fue {result.MeanBps}");
        Assert.Equal(2, result.PerStreamBps.Count);
        Assert.NotNull(result.Intervals);
    }

    [Fact(Timeout = 20_000)]
    public async Task LoopbackUdpDownTest_ReportsPhaseDurationAndPacedRate()
    {
        var (clientCtl, hostCtl) = await OpenControlPairAsync();
        await using var clientCtlAsync = clientCtl;
        await using var hostCtlAsync = hostCtl;
        await using var clientRouter = new ControlMessageRouter(new ControlStream(clientCtl));
        await using var hostRouter = new ControlMessageRouter(new ControlStream(hostCtl));
        clientRouter.Start();
        hostRouter.Start();

        const ulong targetBps = 20_000_000;
        var parms = new TestParameters
        {
            Direction = TestDirection.Down,
            DurationS = 1.0,
            Streams = 1,
            PayloadSize = 1200,
            WarmupS = 0.0,
            Transport = TestTransport.Udp,
            TargetBitrateBps = targetBps,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = MeasurementResponder.RunAsync(
            hostCtl, hostRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
            sessionId: "s-udp", negotiatedCaps: Capabilities.DefaultV1,
            cancellationToken: cts.Token);
        var clientTask = MeasurementSession.RunClientAsync(
            clientCtl, clientRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
            remoteHost: "127.0.0.1",
            inputs: new MeasurementClientInputs
            {
                Parameters = parms,
                PeerName = "host",
                PeerPlatform = PeerPlatform.Linux,
                SessionId = "s-udp",
                NegotiatedCaps = Capabilities.DefaultV1,
            },
            onProgress: null,
            cancellationToken: cts.Token);

        await Task.WhenAll(hostTask, clientTask);
        var result = await clientTask;
        var hostBody = await hostTask;

        // La duración es la ventana de medición, no test_start→result (que
        // sumaba HELLO, graces y udp_stats: ~1.6 s para un test de 1 s).
        Assert.InRange(hostBody.DurationS, 0.8, 1.4);
        Assert.NotNull(result.Udp);
        // Pacing por stream en loopback: tolerancia amplia para CI cargado.
        Assert.InRange((double)result.MeanBps, targetBps * 0.6, targetBps * 1.2);
    }
}
