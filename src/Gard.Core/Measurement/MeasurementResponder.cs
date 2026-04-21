using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>
/// Orquestador del rol host: responde pings, acepta <c>test_start</c>, abre el
/// plano de datos, ejecuta el loop de throughput y emite <c>test_tick</c> +
/// <c>result</c>.
/// </summary>
public static class MeasurementResponder
{
    private sealed record HostPhaseSnapshot(
        ulong MeanBps,
        ulong PeakBps,
        IReadOnlyList<ulong> PerStreamBps,
        ulong TotalBytes,
        double DurS);

    public static async Task<ResultBody> RunAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        IDataPlane dataPlane,
        string sessionId,
        int tickIntervalMs = 200,
        CancellationToken cancellationToken = default)
    {
        // 1. Ping responder en paralelo durante toda la sesión.
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pingTask = PingResponder.RunAsync(controlConnection, router, pingCts.Token);

        try
        {
            // 2. Esperar test_start.
            var (_, parms) = await router.AwaitTestStartAsync(120_000, cancellationToken).ConfigureAwait(false);

            // 3. Abrir N listeners, mandar ack, aceptar conexiones (en ese orden).
            var acceptance = await dataPlane.HostOpenAsync(parms.Streams, cancellationToken).ConfigureAwait(false);
            var ackId = NextRandomId();
            await controlConnection.SendControlAsync(
                new TestStartAckMessage(ackId, new TestStartAckBody
                {
                    Accepted = true,
                    DataPorts = acceptance.Ports,
                }),
                cancellationToken).ConfigureAwait(false);

            var dataConns = await dataPlane.AcceptHostAsync(acceptance, 15_000, cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunAfterDataPlaneReadyAsync(
                    controlConnection, router, dataConns,
                    parms, sessionId, tickIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                foreach (var c in dataConns)
                {
                    try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<ResultBody> RunAfterDataPlaneReadyAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        IReadOnlyList<IFrameTransport> dataConns,
        TestStartBody parms,
        string sessionId,
        int tickIntervalMs,
        CancellationToken cancellationToken)
    {
        var counters = Enumerable.Range(0, parms.Streams).Select(_ => new ByteCounter()).ToArray();
        var globalCounter = new ByteCounter();
        var startedAt = DateTimeOffset.UtcNow;

        var sequentialBidir = parms.Direction == TestDirection.Bidir
            && parms.BidirMode == BidirMode.Sequential;

        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var persistentReceiveTasks = new Task[dataConns.Count];
        for (var i = 0; i < dataConns.Count; i++)
        {
            var cnt = counters[i];
            var conn = dataConns[i];
            persistentReceiveTasks[i] = DataChannels.ReceiveLoopDoubleAsync(
                conn, cnt, globalCounter,
                PeerDataRole.Responder, null,
                recvCts.Token);
        }

        async Task<HostPhaseSnapshot> RunPhaseAsync(TestDirection hostRole)
        {
            foreach (var c in counters) c.Reset();
            globalCounter.Reset();

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTasks = new List<Task>();
            if (hostRole == TestDirection.Down || hostRole == TestDirection.Bidir)
            {
                for (var i = 0; i < dataConns.Count; i++)
                {
                    var cnt = counters[i];
                    var sendCnt = hostRole == TestDirection.Bidir ? new ByteCounter() : cnt;
                    var conn = dataConns[i];
                    sendTasks.Add(DataChannels.SendLoopDoubleAsync(
                        conn, parms.PayloadSize, sendCnt, globalCounter, sendCts.Token));
                }
            }

            if (parms.WarmupS > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(parms.WarmupS), cancellationToken).ConfigureAwait(false);
                foreach (var c in counters) c.Reset();
                globalCounter.Reset();
            }

            var phaseStart = MonotonicClock.NowNs();
            var tickNs = (ulong)tickIntervalMs * 1_000_000UL;
            ulong peakBps = 0;
            ulong accumulated = 0;
            var lastTickNs = phaseStart;
            var nextTickNs = phaseStart + tickNs;
            var phaseEnd = phaseStart + (ulong)(parms.DurationS * 1_000_000_000);

            while (MonotonicClock.NowNs() < phaseEnd)
            {
                try { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                var now = MonotonicClock.NowNs();
                if (now >= nextTickNs)
                {
                    var windowBytes = globalCounter.Drain();
                    accumulated += windowBytes;
                    var windowDurNs = now - lastTickNs;
                    ulong bps = windowDurNs > 0
                        ? (ulong)(windowBytes * 8.0 * 1_000_000_000.0 / windowDurNs)
                        : 0;
                    if (bps > peakBps) peakBps = bps;
                    lastTickNs = now;
                    nextTickNs = now + tickNs;
                    var elapsedS = (now - phaseStart) / 1_000_000_000.0;
                    var tickBody = new TestTickBody
                    {
                        ElapsedS = elapsedS,
                        BytesUp = hostRole == TestDirection.Up ? accumulated : 0,
                        BytesDown = hostRole == TestDirection.Up ? 0 : accumulated,
                        PingAvgMs = 0,
                        JitterMs = 0,
                        LossPct = 0,
                    };
                    try
                    {
                        await controlConnection.SendControlAsync(
                            new TestTickMessage(NextRandomId(), tickBody),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch { /* best effort */ }
                }
            }

            sendCts.Cancel();
            try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { }
            accumulated += globalCounter.Drain();

            var phaseDurS = Math.Max(
                (MonotonicClock.NowNs() - phaseStart) / 1_000_000_000.0, 0.001);
            var phaseMeanBps = (ulong)(accumulated * 8.0 / phaseDurS);
            var phasePerStreamBps = counters.Select(c => (ulong)(c.Value * 8.0 / phaseDurS)).ToArray();

            return new HostPhaseSnapshot(
                phaseMeanBps, peakBps, phasePerStreamBps, accumulated, phaseDurS);
        }

        var phaseSnapshots = new List<HostPhaseSnapshot>();
        if (sequentialBidir)
        {
            var upSnap = await RunPhaseAsync(TestDirection.Up).ConfigureAwait(false);
            var gapS = parms.GapS ?? 0.5;
            await Task.Delay(TimeSpan.FromSeconds(gapS), cancellationToken).ConfigureAwait(false);
            var downSnap = await RunPhaseAsync(TestDirection.Down).ConfigureAwait(false);
            phaseSnapshots.Add(upSnap);
            phaseSnapshots.Add(downSnap);
        }
        else
        {
            phaseSnapshots.Add(await RunPhaseAsync(parms.Direction).ConfigureAwait(false));
        }

        // Esperar test_end del cliente.
        await router.AwaitTestEndAsync(120_000, cancellationToken).ConfigureAwait(false);
        recvCts.Cancel();
        try { await Task.WhenAll(persistentReceiveTasks).ConfigureAwait(false); } catch { }

        var endedAt = DateTimeOffset.UtcNow;
        var headline = phaseSnapshots[^1];
        var totalS = phaseSnapshots.Sum(s => s.DurS);

        ulong aggregatedMeanBps;
        ulong aggregatedPeakBps;
        IReadOnlyList<ulong> aggregatedPerStream;
        ThroughputBody? throughputUp = null;
        ThroughputBody? throughputDown = null;

        if (sequentialBidir)
        {
            var upSnap = phaseSnapshots[0];
            var downSnap = phaseSnapshots[1];
            throughputUp = new ThroughputBody
            {
                MeanBps = upSnap.MeanBps, PeakBps = upSnap.PeakBps, PerStreamBps = upSnap.PerStreamBps,
            };
            throughputDown = new ThroughputBody
            {
                MeanBps = downSnap.MeanBps, PeakBps = downSnap.PeakBps, PerStreamBps = downSnap.PerStreamBps,
            };
            aggregatedMeanBps = upSnap.MeanBps + downSnap.MeanBps;
            aggregatedPeakBps = Math.Max(upSnap.PeakBps, downSnap.PeakBps);
            aggregatedPerStream = downSnap.PerStreamBps;
        }
        else
        {
            aggregatedMeanBps = headline.MeanBps;
            aggregatedPeakBps = headline.PeakBps;
            aggregatedPerStream = headline.PerStreamBps;
        }

        var body = new ResultBody
        {
            SessionId = sessionId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Direction = parms.Direction,
            Streams = parms.Streams,
            DurationS = totalS,
            Throughput = new ThroughputBody
            {
                MeanBps = aggregatedMeanBps,
                PeakBps = aggregatedPeakBps,
                PerStreamBps = aggregatedPerStream,
            },
            LatencyMs = new LatencyBody { Min = 0, Avg = 0, Max = 0, P95 = 0 },
            JitterMs = 0,
            LossPct = 0,
            Samples = 0,
            ThroughputUp = throughputUp,
            ThroughputDown = throughputDown,
            ProtocolVersion = ProtocolVersion.Current,
        };

        await controlConnection.SendControlAsync(
            new ResultMessage(NextRandomId(), body),
            cancellationToken).ConfigureAwait(false);

        return body;
    }

    private static ulong NextRandomId() =>
        (ulong)Random.Shared.NextInt64(1, long.MaxValue);
}
