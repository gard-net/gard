using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Handshake;

public class HandshakeTimeoutTests
{
    private static readonly DeviceIdentity Identity = new()
    {
        Name = "test",
        Platform = PeerPlatform.Linux,
        AppVersion = "gard-test",
    };

    private static async Task<(TcpFrameTransport client, TcpFrameTransport host)> OpenPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var client = await TcpFrameTransport.ConnectAsync("127.0.0.1", port);
        var host = TcpFrameTransport.FromAccepted(await acceptTask);
        listener.Stop();
        return (client, host);
    }

    [Fact(Timeout = 10_000)]
    public async Task Host_TimesOut_WhenPeerNeverSendsHello()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<LandspeedException>(() =>
            HostHandshake.AcceptAsync(host, Identity, timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(LandspeedErrorKind.Timeout, ex.Kind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"tardó {sw.Elapsed}");
    }

    [Fact(Timeout = 10_000)]
    public async Task Client_TimesOut_WhenHostNeverAnswers()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var ex = await Assert.ThrowsAsync<LandspeedException>(() =>
            ClientHandshake.PerformAsync(client, Identity, timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(LandspeedErrorKind.Timeout, ex.Kind);
    }

    [Fact(Timeout = 10_000)]
    public async Task Handshake_StillSucceeds_WithinTimeout()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var hostTask = HostHandshake.AcceptAsync(host, Identity);
        var outcome = await ClientHandshake.PerformAsync(client, Identity);
        var hostOutcome = await hostTask;

        Assert.Equal(hostOutcome.SessionId, outcome.SessionId);
        Assert.True(outcome.NegotiatedCaps.HasFlag(Capabilities.UdpDataPlane));
    }

    [Fact]
    public void Defaults_AreShortWithoutPairing()
    {
        Assert.True(HandshakeTimeouts.Default <= TimeSpan.FromSeconds(30));
        Assert.True(HandshakeTimeouts.WithPairing > HandshakeTimeouts.Default);
    }
}
