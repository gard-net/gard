using System.Net;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Measurement;

/// <summary>
/// Orquestación LSP/1.2 cuando el data-plane es UDP.
/// Soporta <c>Up</c>, <c>Down</c> y <c>Bidir</c> (simultaneous).
///
/// Idea base:
///   - Ambos lados corren senders + receivers según dirección.
///   - Tras <c>test_end</c>, ambos intercambian un <c>udp_stats</c> con stats
///     completas por stream (sent/recv/reorder/dup/jitter).
///   - El host agrega ambos reportes y calcula pérdida por dirección.
///
/// Pérdida por dirección:
///   - Up   (cliente → host): host.received vs cliente.sent
///   - Down (host   → cliente): cliente.received vs host.sent
///   - Bidir: ambas, promedio en el UdpStatsBody agregado.
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
        if (parms.Direction == TestDirection.Bidir && parms.BidirMode == BidirMode.Sequential)
        {
            throw LandspeedException.InternalInconsistency(
                "UDP no soporta bidir secuencial; usar --bidir-mode simultaneous");
        }

        var doSend = parms.Direction is TestDirection.Up or TestDirection.Bidir;
        var doRecv = parms.Direction is TestDirection.Down or TestDirection.Bidir;

        await using var clientSockets = await UdpDataPlane.ClientConnectAsync(
            remoteHost, dataPorts, parms.Streams, ct).ConfigureAwait(false);

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var senders = doSend ? new UdpSender[parms.Streams] : null;
        var sendTasks = doSend ? new Task[parms.Streams] : null;
        var recvStats = doRecv ? new UdpReceiverStats[parms.Streams] : null;
        var recvTasks = doRecv ? new Task[parms.Streams] : null;

        if (doSend)
        {
            // target_bitrate_bps es per-stream (match ref. Swift landspeed).
            var per = parms.TargetBitrateBps;
            for (var i = 0; i < parms.Streams; i++)
            {
                senders![i] = new UdpSender();
                var s = senders[i]; var sock = clientSockets.Sockets[i]; var idx = (ushort)i;
                sendTasks![i] = RunOnDedicatedThread(
                    () => s.RunAsync(sock, idx, parms.PayloadSize, per, sendCts.Token),
                    sendCts.Token);
            }
        }
        if (doRecv)
        {
            for (var i = 0; i < parms.Streams; i++)
            {
                recvStats![i] = new UdpReceiverStats();
                var st = recvStats[i]; var sock = clientSockets.Sockets[i];
                recvTasks![i] = RunOnDedicatedThread(
                    () => UdpReceiver.RunAsync(sock, st, recvCts.Token),
                    recvCts.Token);
            }
        }

        var phaseStart = DateTimeOffset.UtcNow;
        var phaseEnd = phaseStart.AddSeconds(parms.DurationS);
        await RunDurationWithProgressAsync(phaseStart, phaseEnd, senders, recvStats, onProgress, ct)
            .ConfigureAwait(false);

        sendCts.Cancel();
        if (sendTasks is not null) { try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { } }

        await controlConnection.SendControlAsync(
            new TestEndMessage(NextRandomId(), new TestEndBody()), ct).ConfigureAwait(false);

        // Grace para paquetes en vuelo antes de parar recv.
        try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { }
        recvCts.Cancel();
        if (recvTasks is not null) { try { await Task.WhenAll(recvTasks).ConfigureAwait(false); } catch { } }

        var phaseDurS = Math.Max(0.001, (DateTimeOffset.UtcNow - phaseStart).TotalSeconds);

        // Reporte completo al host.
        var myReport = BuildPerStreamReport(parms.Streams, senders, recvStats);
        await controlConnection.SendControlAsync(
            new UdpStatsReportMessage(NextRandomId(),
                new UdpStatsReportBody { PerStream = myReport }),
            ct).ConfigureAwait(false);

        // Esperar reporte del host + result.
        UdpStatsReportBody? hostReport = null;
        try { hostReport = await router.AwaitUdpStatsReportAsync(15_000, ct).ConfigureAwait(false); }
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
            ThroughputUp = hostResult.ThroughputUp,
            ThroughputDown = hostResult.ThroughputDown,
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

    public static async Task<ResultBody> RunHostAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        TestStartBody parms,
        string sessionId,
        CancellationToken ct)
    {
        if (parms.Direction == TestDirection.Bidir && parms.BidirMode == BidirMode.Sequential)
        {
            throw LandspeedException.InternalInconsistency(
                "UDP no soporta bidir secuencial");
        }

        // Host siempre envía si el cliente está en Down/Bidir. Host siempre
        // recibe si el cliente está en Up/Bidir.
        var hostSends = parms.Direction is TestDirection.Down or TestDirection.Bidir;
        var hostRecvs = parms.Direction is TestDirection.Up or TestDirection.Bidir;

        var startedAt = DateTimeOffset.UtcNow;
        await using var hostSockets = UdpDataPlane.HostOpen(parms.Streams, IPAddress.IPv6Any);

        await controlConnection.SendControlAsync(
            new TestStartAckMessage(NextRandomId(), new TestStartAckBody
            {
                Accepted = true,
                DataPorts = hostSockets.Ports,
            }), ct).ConfigureAwait(false);

        // 1) Recibir HELLO_UDP (seq=0) en cada socket → aprendemos endpoint cliente.
        var stats = hostRecvs ? new UdpReceiverStats[parms.Streams] : null;
        var endpoints = await UdpDataPlane.HostDrainHelloAsync(
            hostSockets,
            timeoutMs: 15_000,
            onStrayData: hostRecvs
                ? (idx, r) =>
                {
                    if (UdpDataPlane.TryParseHeader(r.Buffer, out var s, out var ts, out _))
                        stats![idx].OnPacket(s, ts, (ulong)System.Diagnostics.Stopwatch.GetTimestamp(), r.Buffer.Length);
                }
                : null,
            cancellationToken: ct).ConfigureAwait(false);

        // 2) Arrancar senders y/o receivers.
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var senders = hostSends ? new UdpSender[parms.Streams] : null;
        var sendTasks = hostSends ? new Task[parms.Streams] : null;
        var recvTasks = hostRecvs ? new Task[parms.Streams] : null;
        if (hostRecvs)
        {
            stats ??= new UdpReceiverStats[parms.Streams];
            for (var i = 0; i < parms.Streams; i++)
            {
                stats[i] ??= new UdpReceiverStats();
                var s = stats[i]; var sock = hostSockets.Sockets[i];
                recvTasks![i] = RunOnDedicatedThread(
                    () => UdpReceiver.RunAsync(sock, s, recvCts.Token),
                    recvCts.Token);
            }
        }
        if (hostSends)
        {
            // target_bitrate_bps es per-stream (match ref. Swift landspeed).
            var per = parms.TargetBitrateBps ?? 0UL;
            for (var i = 0; i < parms.Streams; i++)
            {
                senders![i] = new UdpSender();
                var s = senders[i]; var sock = hostSockets.Sockets[i];
                var ep = endpoints[i]; var idx = (ushort)i;
                sendTasks![i] = RunOnDedicatedThread(
                    () => s.RunAsync(sock, ep, idx, parms.PayloadSize, per, sendCts.Token),
                    sendCts.Token);
            }
        }

        // 3) Esperar test_end del cliente.
        await router.AwaitTestEndAsync(120_000, ct).ConfigureAwait(false);

        sendCts.Cancel();
        if (sendTasks is not null) { try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { } }

        // Grace para paquetes últimos.
        try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { }
        recvCts.Cancel();
        if (recvTasks is not null) { try { await Task.WhenAll(recvTasks).ConfigureAwait(false); } catch { } }

        // 4) Reporte de stats al cliente.
        var myReport = BuildPerStreamReport(parms.Streams, senders, stats);
        await controlConnection.SendControlAsync(
            new UdpStatsReportMessage(NextRandomId(),
                new UdpStatsReportBody { PerStream = myReport }),
            ct).ConfigureAwait(false);

        // 5) Reporte del cliente.
        UdpStatsReportBody? clientReport = null;
        try { clientReport = await router.AwaitUdpStatsReportAsync(15_000, ct).ConfigureAwait(false); }
        catch { /* seguimos sin */ }

        var endedAt = DateTimeOffset.UtcNow;
        var durationS = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);

        // 6) Agregar por dirección.
        var (upUdp, downUdp, aggUdp, upBps, downBps, perUp, perDown) = Aggregate(
            parms, myReport, clientReport, durationS);

        // Throughput "headline" depende de la dirección.
        ulong meanBps, peakBps;
        IReadOnlyList<ulong> perStreamBps;
        ThroughputBody? thUp = null, thDown = null;
        switch (parms.Direction)
        {
            case TestDirection.Up:
                meanBps = upBps; peakBps = upBps; perStreamBps = perUp;
                break;
            case TestDirection.Down:
                meanBps = downBps; peakBps = downBps; perStreamBps = perDown;
                break;
            case TestDirection.Bidir:
                meanBps = upBps + downBps;
                peakBps = Math.Max(upBps, downBps);
                perStreamBps = perUp;
                thUp = new ThroughputBody { MeanBps = upBps, PeakBps = upBps, PerStreamBps = perUp };
                thDown = new ThroughputBody { MeanBps = downBps, PeakBps = downBps, PerStreamBps = perDown };
                break;
            default:
                throw LandspeedException.InternalInconsistency($"dir desconocida {parms.Direction}");
        }

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
                PeakBps = peakBps,
                PerStreamBps = perStreamBps,
            },
            LatencyMs = new LatencyBody { Min = 0, Avg = 0, Max = 0, P95 = 0 },
            JitterMs = aggUdp.JitterMs,
            LossPct = aggUdp.LossPct,
            Samples = (int)Math.Min(aggUdp.PacketsReceived, int.MaxValue),
            ThroughputUp = thUp,
            ThroughputDown = thDown,
            Udp = aggUdp,
            ProtocolVersion = ProtocolVersion.Current,
        };

        await controlConnection.SendControlAsync(
            new ResultMessage(NextRandomId(), body), ct).ConfigureAwait(false);
        return body;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task RunDurationWithProgressAsync(
        DateTimeOffset phaseStart,
        DateTimeOffset phaseEnd,
        UdpSender[]? senders,
        UdpReceiverStats[]? recvStats,
        Action<MeasurementProgress>? onProgress,
        CancellationToken ct)
    {
        ulong lastBytes = 0;
        var lastTick = phaseStart;
        while (DateTimeOffset.UtcNow < phaseEnd && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var now = DateTimeOffset.UtcNow;
            ulong total = 0;
            if (senders is not null) foreach (var s in senders) total += s.BytesSent;
            if (recvStats is not null) foreach (var s in recvStats) total += s.BytesReceived;
            var winBytes = total - lastBytes;
            var winS = Math.Max(0.001, (now - lastTick).TotalSeconds);
            var bps = (ulong)(winBytes * 8.0 / winS);
            var elapsedS = (now - phaseStart).TotalSeconds;
            onProgress?.Invoke(new ThroughputTickProgress(elapsedS, bps));
            lastBytes = total; lastTick = now;
        }
    }

    private static UdpStatsPerStream[] BuildPerStreamReport(
        int streams, UdpSender[]? senders, UdpReceiverStats[]? recvStats)
    {
        var arr = new UdpStatsPerStream[streams];
        for (var i = 0; i < streams; i++)
        {
            arr[i] = new UdpStatsPerStream
            {
                Stream = i,
                PacketsSent = senders?[i].PacketsSent ?? 0,
                BytesSent = senders?[i].BytesSent ?? 0,
                PacketsReceived = recvStats?[i].PacketsReceived ?? 0,
                BytesReceived = recvStats?[i].BytesReceived ?? 0,
                ReorderCount = recvStats?[i].ReorderCount ?? 0,
                DuplicateCount = recvStats?[i].DuplicateCount ?? 0,
                JitterNs = recvStats?[i].JitterNs ?? 0,
            };
        }
        return arr;
    }

    private sealed record DirAgg(ulong Sent, ulong Received, ulong Bytes, ulong Reorder, ulong Dup, double JitterNs);

    private static DirAgg SumStreams(IReadOnlyList<UdpStatsPerStream>? r)
    {
        if (r is null) return new DirAgg(0, 0, 0, 0, 0, 0);
        ulong s = 0, rec = 0, b = 0, ro = 0, d = 0; double j = 0;
        foreach (var p in r) { s += p.PacketsSent; rec += p.PacketsReceived; b += p.BytesReceived;
            ro += p.ReorderCount; d += p.DuplicateCount; j += p.JitterNs; }
        return new DirAgg(s, rec, b, ro, d, r.Count == 0 ? 0 : j / r.Count);
    }

    private static (UdpStatsBody? up, UdpStatsBody? down, UdpStatsBody agg,
                    ulong upBps, ulong downBps,
                    IReadOnlyList<ulong> perUp, IReadOnlyList<ulong> perDown)
        Aggregate(
            TestStartBody parms,
            IReadOnlyList<UdpStatsPerStream> hostReport,
            UdpStatsReportBody? clientReport,
            double durationS)
    {
        // hostReport = lo que el host midió (sent host→client, recv client→host).
        // clientReport = lo que el cliente midió (sent client→host, recv host→client).
        var hAgg = SumStreams(hostReport);
        var cAgg = SumStreams(clientReport?.PerStream);

        // Up = client→host. Sent: cliente. Received: host.
        ulong upSent = cAgg.Sent, upRecv = hAgg.Received, upBytes = hAgg.Bytes;
        ulong upLost = upSent > upRecv ? upSent - upRecv : 0;
        double upLossPct = upSent > 0 ? upLost * 100.0 / upSent : 0;
        double upJitterMs = hAgg.JitterNs / 1_000_000.0;
        ulong upBps = (ulong)(upBytes * 8.0 / durationS);
        var perUp = hostReport.Select(p => (ulong)(p.BytesReceived * 8.0 / durationS)).ToArray();

        // Down = host→client. Sent: host. Received: cliente.
        ulong dnSent = hAgg.Sent, dnRecv = cAgg.Received, dnBytes = cAgg.Bytes;
        ulong dnLost = dnSent > dnRecv ? dnSent - dnRecv : 0;
        double dnLossPct = dnSent > 0 ? dnLost * 100.0 / dnSent : 0;
        double dnJitterMs = cAgg.JitterNs / 1_000_000.0;
        ulong dnBps = (ulong)(dnBytes * 8.0 / durationS);
        var perDown = clientReport?.PerStream
            .Select(p => (ulong)(p.BytesReceived * 8.0 / durationS)).ToArray()
            ?? new ulong[parms.Streams];

        var target = parms.TargetBitrateBps ?? 0;
        UdpStatsBody? up = null, down = null;
        // `target` es por stream (§12.3). Los m* son agregados → comparar vs agregado.
        double missPct(ulong aggBps)
        {
            if (target == 0) return 0;
            var aggTarget = (double)target * parms.Streams;
            return Math.Abs((double)aggBps - aggTarget) * 100.0 / aggTarget;
        }

        if (parms.Direction is TestDirection.Up or TestDirection.Bidir)
        {
            up = new UdpStatsBody
            {
                PacketsSent = upSent, PacketsReceived = upRecv, PacketsLost = upLost,
                LossPct = upLossPct,
                ReorderCount = hAgg.Reorder, ReorderPct = upRecv > 0 ? hAgg.Reorder * 100.0 / upRecv : 0,
                DuplicateCount = hAgg.Dup,
                JitterMs = upJitterMs, OwdMs = null,
                TargetBitrateBps = target, BitrateMissPct = missPct(upBps),
            };
        }
        if (parms.Direction is TestDirection.Down or TestDirection.Bidir)
        {
            down = new UdpStatsBody
            {
                PacketsSent = dnSent, PacketsReceived = dnRecv, PacketsLost = dnLost,
                LossPct = dnLossPct,
                ReorderCount = cAgg.Reorder, ReorderPct = dnRecv > 0 ? cAgg.Reorder * 100.0 / dnRecv : 0,
                DuplicateCount = cAgg.Dup,
                JitterMs = dnJitterMs, OwdMs = null,
                TargetBitrateBps = target, BitrateMissPct = missPct(dnBps),
            };
        }

        UdpStatsBody agg = parms.Direction switch
        {
            TestDirection.Up   => up!,
            TestDirection.Down => down!,
            TestDirection.Bidir => new UdpStatsBody
            {
                PacketsSent = upSent + dnSent,
                PacketsReceived = upRecv + dnRecv,
                PacketsLost = upLost + dnLost,
                LossPct = (upLossPct + dnLossPct) / 2,
                ReorderCount = hAgg.Reorder + cAgg.Reorder,
                ReorderPct = (up!.ReorderPct + down!.ReorderPct) / 2,
                DuplicateCount = hAgg.Dup + cAgg.Dup,
                JitterMs = (upJitterMs + dnJitterMs) / 2,
                OwdMs = null,
                TargetBitrateBps = target,
                BitrateMissPct = missPct(upBps + dnBps),
            },
            _ => throw LandspeedException.InternalInconsistency($"dir {parms.Direction}"),
        };

        return (up, down, agg, upBps, dnBps, perUp, perDown);
    }

    private static ulong NextRandomId() =>
        (ulong)Random.Shared.NextInt64(1, long.MaxValue);

    /// <summary>
    /// Arranca el loop sender/receiver en un <c>Thread</c> dedicado (fuera del
    /// ThreadPool). Con <c>streams &gt; 1</c>, varios <c>Task.Run</c> en el
    /// ThreadPool se preemptaban mutuamente durante el busy-wait del pacer —
    /// el throughput por stream caía a ~80 µs/pkt (12.5 kpps) cuando en solo
    /// podía bajar a ~10 µs/pkt. Con un hilo dedicado por stream, cada uno
    /// queda en su propio core y el sender puede mantener el target.
    /// </summary>
    private static Task RunOnDedicatedThread(Func<Task> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work().GetAwaiter().GetResult();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "gard-udp",
        };
        thread.Start();
        return tcs.Task;
    }
}
