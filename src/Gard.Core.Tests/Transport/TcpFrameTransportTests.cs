using System.Net;
using System.Net.Sockets;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Tests.Transport;

public class TcpFrameTransportTests
{
    private static async Task<(TcpFrameTransport client, TcpFrameTransport server, TcpListener listener)>
        OpenPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var clientTask = TcpFrameTransport.ConnectAsync("127.0.0.1", port);
        var serverSocket = await acceptTask;
        var client = await clientTask;
        var server = TcpFrameTransport.FromAccepted(serverSocket);
        return (client, server, listener);
    }

    [Fact]
    public async Task SendAndReceive_Heartbeat_RoundTrips()
    {
        var (client, server, listener) = await OpenPairAsync();
        try
        {
            await client.SendAsync(new Frame(FrameType.Heartbeat, Array.Empty<byte>()));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await foreach (var frame in server.Frames(cts.Token))
            {
                Assert.Equal(FrameType.Heartbeat, frame.Type);
                Assert.Empty(frame.Payload.ToArray());
                return;
            }
            Assert.Fail("no se recibió el frame");
        }
        finally
        {
            listener.Stop();
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendControl_RoundTrips()
    {
        var (client, server, listener) = await OpenPairAsync();
        try
        {
            var ping = new PingMessage(42, new PingBody { Seq = 1, SentNs = 123 });
            await client.SendControlAsync(ping);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await foreach (var frame in server.Frames(cts.Token))
            {
                Assert.Equal(FrameType.ControlJson, frame.Type);
                var decoded = ControlMessageCodec.Decode(frame.Payload.Span);
                var pingDecoded = Assert.IsType<PingMessage>(decoded);
                Assert.Equal(42UL, pingDecoded.Id);
                Assert.Equal(1u, pingDecoded.Body.Seq);
                Assert.Equal(123UL, pingDecoded.Body.SentNs);
                return;
            }
            Assert.Fail("no se recibió el frame");
        }
        finally
        {
            listener.Stop();
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleFramesBackToBack_Preserved()
    {
        var (client, server, listener) = await OpenPairAsync();
        try
        {
            const int n = 20;
            for (var i = 0; i < n; i++)
            {
                var payload = BitConverter.GetBytes(i);
                await client.SendAsync(new Frame(FrameType.DataBinary, payload));
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var received = new List<int>();
            await foreach (var frame in server.Frames(cts.Token))
            {
                Assert.Equal(FrameType.DataBinary, frame.Type);
                received.Add(BitConverter.ToInt32(frame.Payload.Span));
                if (received.Count == n) break;
            }
            Assert.Equal(Enumerable.Range(0, n).ToList(), received);
        }
        finally
        {
            listener.Stop();
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RemoteClose_TerminatesFramesStream()
    {
        var (client, server, listener) = await OpenPairAsync();
        listener.Stop();
        try
        {
            await client.DisposeAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await foreach (var _ in server.Frames(cts.Token))
            {
                // no se espera ningún frame; el stream simplemente termina.
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_ToUnreachable_Times_Out()
    {
        // Puerto alto en loopback probablemente libre; si alguien escucha
        // el test se salta su aserción principal sin romperse.
        var ex = await Record.ExceptionAsync(async () =>
        {
            await TcpFrameTransport.ConnectAsync("127.0.0.1", 1, timeoutMs: 200);
        });
        Assert.NotNull(ex);
    }
}
