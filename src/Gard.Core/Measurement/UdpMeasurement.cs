using System.Net;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Measurement;

/// <summary>
/// Orquestación LSP/1.2 cuando el data-plane es UDP. Primera versión soporta
/// <c>Direction=Up</c> (cliente → host). Down/Bidir se rechazan con
/// <see cref="LandspeedException"/> hasta que se implementen.
///
/// Flujo (up):
///   1. Cliente ya hizo ping + <c>test_start</c>. Esta función se llama desde
///      <see cref="MeasurementSession"/> tras recibir <c>test_start_ack</c>.
///   2. Host ya envió <c>test_start_ack</c> con puertos UDP; aquí arranca los
///      receivers.
///   3. Cliente envía durante <c>DurationS</c>, luego <c>test_end</c>.
///   4. Cliente envía <c>udp_stats</c> con packets_sent por stream.
///   5. Host agrega + responde con <c>udp_stats</c> (host no envía; vacío) y
///      luego emite <c>result</c> con <see cref="UdpStatsBody"/> poblado.
/// </summary>
public static class UdpMeasurement
{
    public static async Task<TestResult> RunClientAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        string remoteHost,
        IReadOnlyList<int> dataPorts,
        MeasurementClientInputs inputs,
        PingStats pingStats,
        DateTimeOffset startedAt,
        Action<MeasurementProgress>? onProgress,
        CancellationToken ct)
    {
        var parms = inputs.Parameters;
        if (parms.Direction != TestDirection.Up)
        {
            throw LandspeedException.InternalInconsistency(
                $"UDP actualmente sólo soporta Direction=Up; recibido {parms.Direction}");
        }

        await using var clientSockets = await UdpDataPlane.ClientConnectAsync(
            remoteHost, dataPorts, parms.Streams, ct).ConfigureAwait(false);

        // Un sender por stream. Reparto del bitrate objetivo entre streams.
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var senders = new UdpSender[parms.Streams];
        var sendTasks = new Task[parms.Streams];
        var perStreamTarget = parms.TargetBitrateBps > 0
            ? parms.TargetBitrateBps / (ulong)Math.Max(1, parms.Streams)
            : 0UL;
        for (var i = 0; i < parms.Streams; i++)
        {
            senders[i] = new UdpSender();
            var s = senders[i];
            var sock = clientSockets.Sockets[i];
            var idx = (ushort)i;
            sendTasks[i] = Task.Run(() => s.RunAsync(
                sock, idx, parms.PayloadSize, perStreamTarget, sendCts.Token));
        }

        // Correr durante DurationS, reportando tick de progreso cada 200 ms.
        var phaseStart = DateTimeOffset.UtcNow;
        var phaseEnd = phaseStart.AddSeconds(parms.DurationS);
        ulong lastTotalBytes = 0;
        var lastTick = phaseStart;
        while (DateTimeOffset.UtcNow < phaseEnd && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var now = DateTimeOffset.UtcNow;
            ulong totalBytes = 0;
            foreach (var s in senders) totalBytes += s.BytesSent;
            var winBytes = totalBytes - lastTotalBytes;
            var winS = Math.Max(0.001, (now - lastTick).TotalSeconds);
            var bps = (ulong)(winBytes * 8.0 / winS);
            var elapsedS = (now - phaseStart).TotalSeconds;
            onProgress?.Invoke(new ThroughputTickProgress(elapsedS, bps));
            lastTotalBytes = totalBytes;
            lastTick = now;
        }

        sendCts.Cancel();
        try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { }

        // test_end + udp_stats con packets_sent por stream.
        await controlConnection.SendControlAsync(
            new TestEndMessage(NextRandomId(), new TestEndBody()), ct).ConfigureAwait(false);

        var perStream = new UdpStatsPerStream[parms.Streams];
        for (var i = 0; i < parms.Streams; i++)
        {
            perStream[i] = new UdpStatsPerStream
            {
                Stream = i,
                PacketsSent = senders[i].PacketsSent,
                BytesSent = senders[i].BytesSent,
            };
        }
        await controlConnection.SendControlAsync(
            new UdpStatsReportMessage(NextRandomId(),
                new UdpStatsReportBody { PerStream = perStream }),
            ct).ConfigureAwait(false);

        // Espera stats del host (por simetría) y result final.
        try { await router.AwaitUdpStatsReportAsync(10_000, ct).ConfigureAwait(false); }
        catch { /* no crítico */ }
        var (_, hostResult) = await router.AwaitResultAsync(30_000, ct).ConfigureAwait(false);

        var endedAt = DateTimeOffset.UtcNow;
        var result = new TestResult
        {
            SessionId = inputs.SessionId,
            PeerName = inputs.PeerName,
            PeerPlatform = inputs.PeerPlatform,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Direction = parms.Direction,
            Streams = parms.Streams,
            DurationS = hostResult.DurationS,
            MeanBps = hostResult.Throughput.MeanBps,
            PeakBps = hostResult.Throughput.PeakBps,
            PerStreamBps = hostResult.Throughput.PerStreamBps,
            PingMinMs = pingStats.MinMs,
            PingAvgMs = pingStats.AvgMs,
            PingMaxMs = pingStats.MaxMs,
            PingP95Ms = pingStats.P95Ms,
            JitterMs = hostResult.Udp?.JitterMs ?? pingStats.JitterMs,
            LossPct = hostResult.Udp?.LossPct ?? 0,
            PingSamples = pingStats.Samples.Count,
            Udp = hostResult.Udp,
        };
        onProgress?.Invoke(new FinishedProgress(result));

        try
        {
            await controlConnection.SendControlAsync(
                new GoodbyeMessage(NextRandomId(), new GoodbyeBody { Reason = "test complete" }),
                ct).ConfigureAwait(false);
        }
        catch { /* cortesía */ }
        return result;
    }

    /// <summary>
    /// Host side. Pre-condition: <c>test_start</c> ya recibido (lo pasa el caller).
    /// Abre puertos UDP, manda ack, recibe hasta <c>test_end</c>, construye
    /// <see cref="ResultBody"/> con UDP stats.
    /// </summary>
    public static async Task<ResultBody> RunHostAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        TestStartBody parms,
        string sessionId,
        CancellationToken ct)
    {
        if (parms.Direction != TestDirection.Up)
        {
            throw LandspeedException.InternalInconsistency(
                $"UDP sólo soporta Direction=Up; recibido {parms.Direction}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        await using var hostSockets = UdpDataPlane.HostOpen(parms.Streams, IPAddress.IPv6Any);

        await controlConnection.SendControlAsync(
            new TestStartAckMessage(NextRandomId(), new TestStartAckBody
            {
                Accepted = true,
                DataPorts = hostSockets.Ports,
            }), ct).ConfigureAwait(false);

        var stats = new UdpReceiverStats[parms.Streams];
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvTasks = new Task[parms.Streams];
        for (var i = 0; i < parms.Streams; i++)
        {
            stats[i] = new UdpReceiverStats();
            var sock = hostSockets.Sockets[i];
            var s = stats[i];
            recvTasks[i] = Task.Run(() => UdpReceiver.RunAsync(sock, s, recvCts.Token));
        }

        await router.AwaitTestEndAsync(120_000, ct).ConfigureAwait(false);

        UdpStatsReportBody? clientReport = null;
        try { clientReport = await router.AwaitUdpStatsReportAsync(10_000, ct).ConfigureAwait(false); }
        catch { /* seguimos sin reporte → loss reportado como 0 */ }

        // Grace period para paquetes en vuelo.
        try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { }
        recvCts.Cancel();
        try { await Task.WhenAll(recvTasks).ConfigureAwait(false); } catch { }

        var endedAt = DateTimeOffset.UtcNow;
        var durationS = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);

        ulong totalReceived = 0, totalBytes = 0, totalReorder = 0, totalDup = 0;
        double jitterSum = 0;
        foreach (var s in stats)
        {
            totalReceived += s.PacketsReceived;
            totalBytes += s.BytesReceived;
            totalReorder += s.ReorderCount;
            totalDup += s.DuplicateCount;
            jitterSum += s.JitterNs;
        }
        var jitterMs = stats.Length > 0 ? (jitterSum / stats.Length) / 1_000_000.0 : 0;

        ulong totalSent = 0;
        if (clientReport is not null)
        {
            foreach (var p in clientReport.PerStream) totalSent += p.PacketsSent;
        }
        var packetsLost = totalSent > totalReceived ? totalSent - totalReceived : 0UL;
        var lossPct = totalSent > 0 ? packetsLost * 100.0 / totalSent : 0;
        var reorderPct = totalReceived > 0 ? totalReorder * 100.0 / totalReceived : 0;

        var meanBps = (ulong)(totalBytes * 8.0 / durationS);
        var perStreamBps = stats.Select(s => (ulong)(s.BytesReceived * 8.0 / durationS)).ToArray();

        var target = parms.TargetBitrateBps ?? 0;
        var missPct = target > 0
            ? Math.Abs((double)meanBps - (double)target) * 100.0 / target
            : 0;

        var udp = new UdpStatsBody
        {
            PacketsSent = totalSent,
            PacketsReceived = totalReceived,
            PacketsLost = packetsLost,
            LossPct = lossPct,
            ReorderCount = totalReorder,
            ReorderPct = reorderPct,
            DuplicateCount = totalDup,
            JitterMs = jitterMs,
            OwdMs = null,
            TargetBitrateBps = target,
            BitrateMissPct = missPct,
        };

        // Enviar nuestro udp_stats (host sólo recibió → per-stream vacíos).
        var hostPerStream = Enumerable.Range(0, parms.Streams).Select(i => new UdpStatsPerStream
        {
            Stream = i,
            PacketsSent = 0,
            BytesSent = 0,
        }).ToArray();
        await controlConnection.SendControlAsync(
            new UdpStatsReportMessage(NextRandomId(),
                new UdpStatsReportBody { PerStream = hostPerStream }),
            ct).ConfigureAwait(false);

        var body = new ResultBody
        {
            SessionId = sessionId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Direction = parms.Direction,
            Streams = parms.Streams,
            DurationS = durationS,
            Throughput = new ThroughputBody
            {
                MeanBps = meanBps,
                PeakBps = meanBps,
                PerStreamBps = perStreamBps,
            },
            LatencyMs = new LatencyBody { Min = 0, Avg = 0, Max = 0, P95 = 0 },
            JitterMs = jitterMs,
            LossPct = lossPct,
            Samples = (int)Math.Min(totalReceived, int.MaxValue),
            Udp = udp,
            ProtocolVersion = ProtocolVersion.Current,
        };

        await controlConnection.SendControlAsync(
            new ResultMessage(NextRandomId(), body), ct).ConfigureAwait(false);
        return body;
    }

    private static ulong NextRandomId() =>
        (ulong)Random.Shared.NextInt64(1, long.MaxValue);
}
