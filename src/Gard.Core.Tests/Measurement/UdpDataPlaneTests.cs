using System.Net;
using Gard.Core.Measurement;
using Gard.Core.Utils;

namespace Gard.Core.Tests.Measurement;

public class UdpDataPlaneTests
{
    [Fact]
    public void Header_RoundTrip()
    {
        var buf = new byte[UdpDataPlane.HeaderSize];
        UdpDataPlane.WriteHeader(buf, seq: 0xAABBCCDD_11223344UL, tsNs: 0x1234_5678_ABCD_EF01UL, stream: 7);
        Assert.True(UdpDataPlane.TryParseHeader(buf, out var seq, out var ts, out var stream));
        Assert.Equal(0xAABBCCDD_11223344UL, seq);
        Assert.Equal(0x1234_5678_ABCD_EF01UL, ts);
        Assert.Equal((ushort)7, stream);
    }

    [Fact]
    public void TryParseHeader_TooShort_ReturnsFalse()
    {
        Assert.False(UdpDataPlane.TryParseHeader(new byte[10], out _, out _, out _));
    }

    [Fact]
    public async Task Loopback_SingleStream_ReceivesMostPacketsWithLowJitter()
    {
        // Host abre 1 puerto UDP en loopback, cliente envía N paquetes a
        // ~10 Mbps durante ~1 segundo, receptor loop lee y acumula stats.
        await using var host = UdpDataPlane.HostOpen(count: 1, bindAddress: IPAddress.Loopback);
        Assert.Single(host.Ports);

        await using var client = await UdpDataPlane.ClientConnectAsync(
            "127.0.0.1", host.Ports, streamsCount: 1);

        // Leemos el HELLO_UDP inicial para aprender la 5-tupla de retorno y
        // dejarlo fuera del stats.
        var recvSocket = host.Sockets[0];
        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var hello = await recvSocket.ReceiveAsync(helloCts.Token);
        Assert.True(UdpDataPlane.TryParseHeader(hello.Buffer, out var helloSeq, out _, out _));
        Assert.Equal(0UL, helloSeq);

        var stats = new UdpReceiverStats();
        using var cts = new CancellationTokenSource();

        var sender = new UdpSender();
        var sendTask = sender.RunAsync(
            client.Sockets[0], streamId: 0, payloadSize: 1200,
            targetBitrateBps: 10_000_000, // 10 Mbps
            cts.Token);

        var recvTask = UdpReceiver.RunAsync(recvSocket, stats, cts.Token);

        await Task.Delay(1000);
        cts.Cancel();
        try { await Task.WhenAll(sendTask, recvTask); } catch { }

        // 10 Mbps × 1s / (1200 B × 8) ≈ 1041 paquetes esperados.
        Assert.InRange(sender.PacketsSent, 500UL, 1500UL);
        // Loopback debería perder ~0 paquetes; tolerancia generosa por ruido CI.
        var lost = sender.PacketsSent > stats.PacketsReceived
            ? sender.PacketsSent - stats.PacketsReceived
            : 0UL;
        var lossPct = sender.PacketsSent > 0 ? lost * 100.0 / sender.PacketsSent : 0;
        Assert.True(lossPct < 5.0, $"loss muy alto en loopback: {lossPct:F2}%");

        // Jitter en loopback debería ser sub-milisegundo. 10 ms es un techo holgado.
        var jitterMs = stats.JitterNs / 1_000_000.0;
        Assert.True(jitterMs < 10.0, $"jitter muy alto en loopback: {jitterMs:F2} ms");
    }

    [Fact]
    public void UdpReceiverStats_DetectsDuplicateAndReorder()
    {
        var stats = new UdpReceiverStats();
        var now = MonotonicClock.NowNs();
        stats.OnPacket(seq: 1, sendTsNs: now, recvTsNs: now + 1000, byteCount: 100);
        stats.OnPacket(seq: 2, sendTsNs: now + 100, recvTsNs: now + 1100, byteCount: 100);
        stats.OnPacket(seq: 3, sendTsNs: now + 200, recvTsNs: now + 1200, byteCount: 100);
        stats.OnPacket(seq: 2, sendTsNs: now + 300, recvTsNs: now + 1300, byteCount: 100); // duplicate
        stats.OnPacket(seq: 4, sendTsNs: now + 400, recvTsNs: now + 1400, byteCount: 100);
        stats.OnPacket(seq: 3, sendTsNs: now + 500, recvTsNs: now + 1500, byteCount: 100); // also duplicate (3 ya visto)

        Assert.Equal(4UL, stats.PacketsReceived);
        Assert.Equal(2UL, stats.DuplicateCount);
        Assert.Equal(4UL, stats.MaxSeqSeen);
        Assert.Equal(0UL, stats.ReorderCount); // todos los no-duplicados llegaron en orden
    }
}
