using System.Net;
using System.Net.Sockets;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Handshake;

/// <summary>Pairing de punta a punta sobre sockets loopback reales (spec §7, §8).</summary>
public class PairingHandshakeTests
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

    private static Func<CancellationToken, Task<string>> Code(string code) => _ => Task.FromResult(code);

    private static Task<HandshakeOutcome> Accept(TcpFrameTransport host, IPairingValidator pairing) =>
        HostHandshake.AcceptAsync(host, Identity, pairing: pairing, remoteAddress: IPAddress.Loopback);

    [Fact(Timeout = 10_000)]
    public async Task CorrectCode_CompletesHandshake()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var hostTask = Accept(host, new CodePairingValidator("482913"));
        var outcome = await ClientHandshake.PerformAsync(client, Identity, Code("482913"));
        var hostOutcome = await hostTask;

        Assert.Equal(hostOutcome.SessionId, outcome.SessionId);
    }

    [Fact(Timeout = 10_000)]
    public async Task WrongCode_IsRejectedOnBothSides()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var hostTask = Accept(host, new CodePairingValidator("482913"));
        var clientEx = await Assert.ThrowsAsync<LandspeedException>(
            () => ClientHandshake.PerformAsync(client, Identity, Code("000000")));
        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);

        Assert.Equal(LandspeedErrorKind.PairingInvalid, clientEx.Kind);
        Assert.Equal("pairing_code inválido", clientEx.Context["reason"]);
        Assert.Equal(LandspeedErrorKind.PairingInvalid, hostEx.Kind);
    }

    [Fact(Timeout = 10_000)]
    public async Task ClientWithoutCode_FailsWithPairingRequired()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;

        var hostTask = Accept(host, new CodePairingValidator("482913"));
        var ex = await Assert.ThrowsAsync<LandspeedException>(
            () => ClientHandshake.PerformAsync(client, Identity));
        Assert.Equal(LandspeedErrorKind.PairingRequired, ex.Kind);

        await client.DisposeAsync();
        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.PairingRequired, hostEx.Kind);
    }

    [Fact(Timeout = 10_000)]
    public async Task PeerSkippingPairing_Gets3002()
    {
        var (client, host) = await OpenPairAsync();
        await using var c = client;
        await using var h = host;
        var hostTask = Accept(host, new CodePairingValidator("482913"));

        // Cliente "a mano" que ignora requires_pairing y sigue con clock_sync.
        await using var stream = new ControlStream(client);
        await client.SendControlAsync(new HelloMessage(1, new HelloBody
        {
            ProtocolVersion = ProtocolVersion.Current,
            AppVersion = "rogue",
            Platform = PeerPlatform.Linux,
            DeviceName = "rogue",
            Caps = Capabilities.DefaultV1,
            Nonce = "AAAA",
        }));
        var ack = Assert.IsType<HelloAckMessage>(await stream.NextAsync());
        Assert.True(ack.Body.RequiresPairing);
        await client.SendControlAsync(new ClockSyncMessage(2, new ClockSyncBody { T0Ns = 1 }));

        var err = Assert.IsType<ErrorMessage>(await stream.NextAsync());
        Assert.Equal(3002, err.Body.Code);
        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        Assert.Equal(LandspeedErrorKind.PairingRequired, hostEx.Kind);
    }

    [Fact(Timeout = 30_000)]
    public async Task RepeatedFailures_AreRateLimited_AcrossConnections()
    {
        var validator = new CodePairingValidator("482913");
        for (var i = 0; i < CodePairingValidator.MaxFailuresPerSourcePerWindow; i++)
        {
            var (client, host) = await OpenPairAsync();
            await using var c = client;
            await using var h = host;
            var hostTask = Accept(host, validator);
            await Assert.ThrowsAsync<LandspeedException>(() => ClientHandshake.PerformAsync(client, Identity, Code("000000")));
            await Assert.ThrowsAsync<LandspeedException>(() => hostTask);
        }

        // Reconectar no resetea el contador: ni el código correcto pasa ahora.
        var (client2, host2) = await OpenPairAsync();
        await using var c2 = client2;
        await using var h2 = host2;
        var lastHost = Accept(host2, validator);
        var ex = await Assert.ThrowsAsync<LandspeedException>(
            () => ClientHandshake.PerformAsync(client2, Identity, Code("482913")));
        Assert.Equal(LandspeedErrorKind.PairingInvalid, ex.Kind);
        Assert.Contains("demasiados intentos", (string)ex.Context["reason"]!);
        var hostEx = await Assert.ThrowsAsync<LandspeedException>(() => lastHost);
        Assert.Equal(LandspeedErrorKind.PairingRateLimited, hostEx.Kind);
    }
}
