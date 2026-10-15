using System.Net;
using System.Net.Sockets;
using Gard.Cli;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Measurement;

/// <summary>Hallazgos de pruebas reales contra un host LSP/1.1 (Mac) y por Wi-Fi.</summary>
[Collection(nameof(E2ETestCollection))]
public class PeerCompatibilityTests
{
    private static TestParameters Params(TestTransport transport = TestTransport.Tcp, TestDirection dir = TestDirection.Down,
        BidirMode? mode = null) => new()
    {
        Direction = dir, DurationS = 1, Streams = 2, PayloadSize = 1200, WarmupS = 0,
        Transport = transport, BidirMode = mode,
    };

    [Fact]
    public void Udp_AgainstLsp11Peer_FailsFastWithTypedError()
    {
        var lsp11 = Capabilities.DefaultV1 & ~Capabilities.UdpDataPlane; // caps "fb"
        var ex = Assert.Throws<LandspeedException>(() =>
            MeasurementSession.EnsureSupportedByPeer(Params(TestTransport.Udp), lsp11));
        Assert.Equal(LandspeedErrorKind.UdpNotNegotiated, ex.Kind);
        Assert.Contains("does not support UDP", CliErrors.Describe(ex));
    }

    [Fact]
    public void SequentialBidir_WithoutCap_IsRejectedInsteadOfSilentlyDegraded()
    {
        var caps = Capabilities.DefaultV1 & ~Capabilities.BidirSequential;
        var ex = Assert.Throws<LandspeedException>(() =>
            MeasurementSession.EnsureSupportedByPeer(Params(dir: TestDirection.Bidir, mode: BidirMode.Sequential), caps));
        Assert.Equal(LandspeedErrorKind.TestRejected, ex.Kind);
    }

    [Fact]
    public void SupportedTests_PassTheCheck()
    {
        MeasurementSession.EnsureSupportedByPeer(Params(TestTransport.Udp), Capabilities.DefaultV1);
        MeasurementSession.EnsureSupportedByPeer(Params(), Capabilities.ParallelStreams);
    }

    [Fact]
    public void ConnectionClosed_HasEnglishActionableMessage()
    {
        var message = CliErrors.Describe(LandspeedException.ConnectionClosed());
        Assert.Contains("closed the connection unexpectedly", message);
        Assert.DoesNotContain("cerrado", message);
    }

    [Fact(Timeout = 20_000)]
    public async Task SimultaneousTcpBidir_ReportsUpAndDownSeparately()
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
            Direction = TestDirection.Bidir, BidirMode = BidirMode.Simultaneous,
            DurationS = 1, Streams = 2, PayloadSize = 16_384, WarmupS = 0,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = MeasurementResponder.RunAsync(
            hostCtl, hostRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback },
            sessionId: "bd", negotiatedCaps: Capabilities.DefaultV1, cancellationToken: cts.Token);
        var clientTask = MeasurementSession.RunClientAsync(
            clientCtl, clientRouter, new TcpDataPlane { BindAddress = IPAddress.Loopback }, "127.0.0.1",
            new MeasurementClientInputs
            {
                Parameters = parms, PeerName = "h", PeerPlatform = PeerPlatform.Linux,
                SessionId = "bd", NegotiatedCaps = Capabilities.DefaultV1,
            },
            cancellationToken: cts.Token);
        await Task.WhenAll(hostTask, clientTask);
        var r = await clientTask;

        Assert.NotNull(r.ThroughputUp);
        Assert.NotNull(r.ThroughputDown);
        Assert.True(r.ThroughputUp!.MeanBps > 0);
        Assert.True(r.ThroughputDown!.MeanBps > 0);
        Assert.Equal(r.ThroughputUp.MeanBps + r.ThroughputDown.MeanBps, r.MeanBps);
        Assert.Equal(2, r.ThroughputUp.PerStreamBps.Count);
        Assert.Equal(2, r.ThroughputDown.PerStreamBps.Count);
        // per_stream total = subida + bajada de cada stream (antes: sólo lo recibido).
        Assert.Equal(r.MeanBps, r.PerStreamBps.Aggregate(0UL, (a, b) => a + b), (double)r.MeanBps * 0.02);
    }
}

public class BufferbloatTests
{
    private static TestResult Result(double baseline, double p95) => new()
    {
        SessionId = "s", PeerName = "p", PeerPlatform = PeerPlatform.Macos,
        StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch,
        Direction = TestDirection.Down, Streams = 4, DurationS = 5,
        MeanBps = 41_000_000, PeakBps = 89_000_000, PerStreamBps = [],
        PingMinMs = 3, PingAvgMs = 50, PingMaxMs = 900, PingP95Ms = 120,
        JitterMs = 62, LossPct = 26.7, PingSamples = 30,
        RttUnderLoadMs = new RttUnderLoadStats { Samples = 40, BaselineMedianMs = baseline, P95Ms = p95, MedianMs = p95 / 2 },
    };

    [Fact]
    public void Detected_WhenLoadedLatencyExplodes()
    {
        // Caso real: Mac por Wi-Fi, RTT bajo carga p95 ≈ 2 s.
        var b = CliHumanFormatter.Bufferbloat(Result(baseline: 8, p95: 2005));
        Assert.NotNull(b);
        Assert.Equal(2005, b!.Value.LoadedMs);

        var term = new CliTerminal(CliStyleOptions.Default with { NoColor = true }, forceInteractive: true);
        Assert.Contains("Bufferbloat", CliHumanFormatter.Format(Result(8, 2005), term));
        Assert.Contains("Bufferbloat:", CliHumanFormatter.Format(Result(8, 2005)));
        Assert.Contains("\"bufferbloat\"", CliJson.Serialize(TestReport.From(Result(8, 2005),
            new TestParameters { Direction = TestDirection.Down, DurationS = 5, Streams = 4 }, null)));
    }

    [Theory]
    [InlineData(50, 120)]   // sube, pero menos de 5×
    [InlineData(2, 60)]     // 30× pero bajo 100 ms absolutos
    public void NotDetected_ForModestIncreases(double baseline, double p95)
    {
        Assert.Null(CliHumanFormatter.Bufferbloat(Result(baseline, p95)));
    }
}
