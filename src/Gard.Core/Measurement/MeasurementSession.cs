using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>Inputs para el orquestador cliente.</summary>
public sealed record MeasurementClientInputs
{
    public required TestParameters Parameters { get; init; }
    public required string PeerName { get; init; }
    public required PeerPlatform PeerPlatform { get; init; }
    public required string SessionId { get; init; }
    /// <summary>
    /// Capacidades negociadas (intersección hello/hello_ack). Si incluye
    /// <see cref="Capabilities.DataEcho"/>, el cliente inyecta probes y mide
    /// RTT bajo carga. Por defecto <see cref="Capabilities.DefaultV1"/>.
    /// </summary>
    public Capabilities NegotiatedCaps { get; init; } = Capabilities.DefaultV1;
}

/// <summary>Evento de progreso para la UI durante la medición.</summary>
public abstract record MeasurementProgress;
public sealed record PingPhaseDoneProgress(PingStats Stats) : MeasurementProgress;
public sealed record ThroughputTickProgress(double ElapsedS, ulong InstantaneousBps) : MeasurementProgress;
public sealed record FinishedProgress(TestResult Result) : MeasurementProgress;

/// <summary>
/// Orquestador del rol cliente: ping → throughput con loss-probe + RTT bajo
/// carga + interval reporting opcionales (LSP/1.1).
/// </summary>
public static class MeasurementSession
{
    private sealed record PhaseSnapshot(
        ulong MeanBps,
        ulong PeakBps,
        IReadOnlyList<ulong> PerStreamBps,
        ulong TotalBytes,
        double DurS,
        IReadOnlyList<IntervalSample> IntervalSamples);

    public static async Task<TestResult> RunClientAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        IDataPlane dataPlane,
        string remoteHost,
        MeasurementClientInputs inputs,
        Action<MeasurementProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var parms = inputs.Parameters;

        // 1. Fase PING/JITTER (§5.4): 20 pings @ 50 ms, descarta el primero.
        var pingStats = await PingEngine.RunFixedCountAsync(
            controlConnection, router,
            count: 20, intervalMs: 50, timeoutMs: 500,
            startingSeq: 1, discardFirst: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke(new PingPhaseDoneProgress(pingStats));

        // 2. Enviar test_start y esperar ack con los puertos.
        var testStartId = NextRandomId();
        await controlConnection.SendControlAsync(
            new TestStartMessage(testStartId, parms.AsWireBody()),
            cancellationToken).ConfigureAwait(false);
        var ack = await router.AwaitTestStartAckAsync(10_000, cancellationToken).ConfigureAwait(false);
        if (!ack.Accepted)
        {
            throw LandspeedException.Transport($"host rechazó test_start: {ack.Reason ?? "sin motivo"}");
        }
        if (ack.DataPorts.Count != parms.Streams)
        {
            throw LandspeedException.Transport(
                $"host devolvió {ack.DataPorts.Count} puertos, esperados {parms.Streams}");
        }

        // 3. Conectar N sockets al host.
        var dataConns = await dataPlane.ClientConnectAsync(
            remoteHost, ack.DataPorts, timeoutMs: 10_000, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAfterDataPlaneReadyAsync(
                controlConnection, router, dataConns,
                parms, inputs, pingStats, startedAt,
                onProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var c in dataConns)
            {
                try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task<TestResult> RunAfterDataPlaneReadyAsync(
        IFrameTransport controlConnection,
        ControlMessageRouter router,
        IReadOnlyList<IFrameTransport> dataConns,
        TestParameters parms,
        MeasurementClientInputs inputs,
        PingStats pingStats,
        DateTimeOffset startedAt,
        Action<MeasurementProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        // 4. Contadores.
        var counters = Enumerable.Range(0, parms.Streams).Select(_ => new ByteCounter()).ToArray();
        var globalCounter = new ByteCounter();

        var echoEnabled = inputs.NegotiatedCaps.HasFlag(Capabilities.DataEcho);
        var rttCollector = echoEnabled ? new RttSampleCollector() : null;
        var intervalReportingEnabled = inputs.NegotiatedCaps.HasFlag(Capabilities.IntervalReporting);

        var sequentialBidir = parms.Direction == TestDirection.Bidir
            && parms.BidirMode == BidirMode.Sequential
            && inputs.NegotiatedCaps.HasFlag(Capabilities.BidirSequential);

        // 5. Loss probe continuo (§5.6). Se cancela con lossProbeCts.
        using var lossProbeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lossProbeTask = PingEngine.RunLossProbeAsync(
            controlConnection, router,
            intervalMs: 100, timeoutMs: 500, startingSeq: 10_000,
            cancellationToken: lossProbeCts.Token);

        // Echo probe (LSP/1.1): sólo en stream 0.
        using var echoProbeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? echoProbeTask = null;
        if (echoEnabled && dataConns.Count > 0)
        {
            echoProbeTask = DataChannels.EchoProbeLoopAsync(
                dataConns[0], intervalMs: 100, cancellationToken: echoProbeCts.Token);
        }

        // Persistent receive loops: arrancan una vez, corren durante todo el test.
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var persistentReceiveTasks = new Task[dataConns.Count];
        for (var i = 0; i < dataConns.Count; i++)
        {
            var cnt = counters[i];
            var collector = (i == 0) ? rttCollector : null;
            var conn = dataConns[i];
            persistentReceiveTasks[i] = DataChannels.ReceiveLoopDoubleAsync(
                conn, cnt, globalCounter,
                PeerDataRole.Initiator, collector,
                recvCts.Token);
        }

        async Task<PhaseSnapshot> RunPhaseAsync(TestDirection phaseDirection)
        {
            const ulong WindowNs = 200_000_000;
            foreach (var c in counters) c.Reset();
            globalCounter.Reset();

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTasks = new List<Task>();
            if (phaseDirection == TestDirection.Up || phaseDirection == TestDirection.Bidir)
            {
                for (var i = 0; i < dataConns.Count; i++)
                {
                    var cnt = counters[i];
                    var sendCnt = phaseDirection == TestDirection.Bidir ? new ByteCounter() : cnt;
                    var conn = dataConns[i];
                    sendTasks.Add(DataChannels.SendLoopDoubleAsync(
                        conn, parms.PayloadSize, sendCnt, globalCounter, sendCts.Token));
                }
            }

            // Warmup.
            if (parms.WarmupS > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(parms.WarmupS), cancellationToken).ConfigureAwait(false);
                _ = globalCounter.Drain();
                foreach (var c in counters) c.Reset();
            }

            var samples = new List<IntervalSample>();
            var phaseStart = MonotonicClock.NowNs();
            var phaseEnd = phaseStart + (ulong)(parms.DurationS * 1_000_000_000);
            ulong phasePeakBps = 0;
            ulong measuredBytesTotal = 0;
            var windowStart = phaseStart;
            var nextWindow = phaseStart + WindowNs;
            while (MonotonicClock.NowNs() < phaseEnd)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                var now = MonotonicClock.NowNs();
                if (now >= nextWindow)
                {
                    var windowBytes = globalCounter.Drain();
                    measuredBytesTotal += windowBytes;
                    var bps = windowBytes * 40;  // 200 ms * 40 = 8 s → bps (bits per second con ×8)... wait, mismo que Swift.
                    // Swift: bps = windowBytes &* 40. Esto asume ventana fija de 200ms.
                    //   windowBytes / 0.2 s = windowBytes * 5 bytes/s → × 8 bits = windowBytes * 40 bits/s. OK.
                    if (bps > phasePeakBps) phasePeakBps = bps;
                    if (intervalReportingEnabled)
                    {
                        samples.Add(new IntervalSample
                        {
                            StartS = (windowStart - phaseStart) / 1_000_000_000.0,
                            EndS = (now - phaseStart) / 1_000_000_000.0,
                            Bps = bps,
                        });
                    }
                    onProgress?.Invoke(new ThroughputTickProgress(
                        (now - phaseStart) / 1_000_000_000.0, bps));
                    windowStart = now;
                    nextWindow = now + WindowNs;
                }
            }
            measuredBytesTotal += globalCounter.Drain();

            var phaseDurS = Math.Max(
                (MonotonicClock.NowNs() - phaseStart) / 1_000_000_000.0, 0.001);
            var phaseMeanBps = (ulong)(measuredBytesTotal * 8.0 / phaseDurS);
            var phasePerStreamBps = counters.Select(c => (ulong)(c.Value * 8.0 / phaseDurS)).ToArray();

            sendCts.Cancel();
            try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { }

            return new PhaseSnapshot(
                phaseMeanBps, phasePeakBps, phasePerStreamBps,
                measuredBytesTotal, phaseDurS, samples);
        }

        var phaseSnapshots = new List<PhaseSnapshot>();
        var phaseDirections = new List<TestDirection>();

        if (sequentialBidir)
        {
            var upSnap = await RunPhaseAsync(TestDirection.Up).ConfigureAwait(false);
            var gapS = parms.GapS ?? 0.5;
            await Task.Delay(TimeSpan.FromSeconds(gapS), cancellationToken).ConfigureAwait(false);
            var downSnap = await RunPhaseAsync(TestDirection.Down).ConfigureAwait(false);
            phaseSnapshots.Add(upSnap);
            phaseSnapshots.Add(downSnap);
            phaseDirections.Add(TestDirection.Up);
            phaseDirections.Add(TestDirection.Down);
        }
        else
        {
            var snap = await RunPhaseAsync(parms.Direction).ConfigureAwait(false);
            phaseSnapshots.Add(snap);
            phaseDirections.Add(parms.Direction);
        }

        var headline = phaseSnapshots[^1];
        var clientMeanBps = headline.MeanBps;
        var peakBps = phaseSnapshots.Aggregate(0UL, (acc, s) => Math.Max(acc, s.PeakBps));
        var clientPerStreamBps = headline.PerStreamBps;

        var intervalSamples = new List<IntervalSample>();
        if (intervalReportingEnabled)
        {
            var offsetS = 0.0;
            foreach (var snap in phaseSnapshots)
            {
                foreach (var s in snap.IntervalSamples)
                {
                    intervalSamples.Add(new IntervalSample
                    {
                        StartS = s.StartS + offsetS,
                        EndS = s.EndS + offsetS,
                        Bps = s.Bps,
                    });
                }
                offsetS += snap.DurS + (sequentialBidir ? (parms.GapS ?? 0.5) : 0);
            }
        }
        const int WindowMs = 200;

        // Cerrar probes y mandar test_end.
        lossProbeCts.Cancel();
        echoProbeCts.Cancel();
        recvCts.Cancel();

        await controlConnection.SendControlAsync(
            new TestEndMessage(NextRandomId(), new TestEndBody()),
            cancellationToken).ConfigureAwait(false);

        PingStats lossProbe;
        try { lossProbe = await lossProbeTask.ConfigureAwait(false); }
        catch { lossProbe = new PingStats(Array.Empty<PingSample>()); }

        if (echoProbeTask is not null)
        {
            try { await echoProbeTask.ConfigureAwait(false); } catch { }
        }

        var (_, hostResult) = await router.AwaitResultAsync(30_000, cancellationToken).ConfigureAwait(false);

        var endedAt = DateTimeOffset.UtcNow;
        var mergedPing = CombinePingStats(pingStats, lossProbe);

        ulong finalMeanBps;
        ulong finalPeakBps;
        IReadOnlyList<ulong> finalPerStreamBps;
        ThroughputBody? finalThroughputUp = null;
        ThroughputBody? finalThroughputDown = null;

        switch (parms.Direction)
        {
            case TestDirection.Down:
                finalMeanBps = clientMeanBps;
                finalPeakBps = peakBps;
                finalPerStreamBps = clientPerStreamBps;
                break;
            case TestDirection.Up:
                finalMeanBps = hostResult.Throughput.MeanBps;
                finalPeakBps = hostResult.Throughput.PeakBps;
                finalPerStreamBps = hostResult.Throughput.PerStreamBps;
                break;
            case TestDirection.Bidir:
                if (sequentialBidir)
                {
                    var upTh = hostResult.ThroughputUp
                        ?? new ThroughputBody { MeanBps = 0, PeakBps = 0, PerStreamBps = Array.Empty<ulong>() };
                    var downSnap = phaseSnapshots[1];
                    var downTh = new ThroughputBody
                    {
                        MeanBps = downSnap.MeanBps,
                        PeakBps = downSnap.PeakBps,
                        PerStreamBps = downSnap.PerStreamBps,
                    };
                    finalThroughputUp = upTh;
                    finalThroughputDown = downTh;
                    finalMeanBps = upTh.MeanBps + downTh.MeanBps;
                    finalPeakBps = Math.Max(upTh.PeakBps, downTh.PeakBps);
                    finalPerStreamBps = downTh.PerStreamBps;
                }
                else
                {
                    finalMeanBps = hostResult.Throughput.MeanBps;
                    finalPeakBps = Math.Max(hostResult.Throughput.PeakBps, peakBps);
                    finalPerStreamBps = hostResult.Throughput.PerStreamBps;
                }
                break;
            default:
                throw LandspeedException.InternalInconsistency($"dirección desconocida: {parms.Direction}");
        }

        RttUnderLoadStats? rttUnderLoad = null;
        if (rttCollector is not null)
        {
            var snapshot = rttCollector.Snapshot();
            rttUnderLoad = snapshot.Samples > 0 ? snapshot : null;
        }

        IntervalReport? intervals = intervalReportingEnabled && intervalSamples.Count > 0
            ? IntervalReportBuilder.FromSamples(WindowMs, intervalSamples)
            : null;

        var result = new TestResult
        {
            SessionId = inputs.SessionId,
            PeerName = inputs.PeerName,
            PeerPlatform = inputs.PeerPlatform,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Direction = parms.Direction,
            Streams = parms.Streams,
            DurationS = parms.DurationS,
            MeanBps = finalMeanBps,
            PeakBps = finalPeakBps,
            PerStreamBps = finalPerStreamBps,
            PingMinMs = mergedPing.MinMs,
            PingAvgMs = mergedPing.AvgMs,
            PingMaxMs = mergedPing.MaxMs,
            PingP95Ms = mergedPing.P95Ms,
            JitterMs = mergedPing.JitterMs,
            LossPct = mergedPing.LossPct,
            PingSamples = mergedPing.Samples.Count,
            RttUnderLoadMs = rttUnderLoad,
            Intervals = intervals,
            ThroughputUp = finalThroughputUp,
            ThroughputDown = finalThroughputDown,
        };

        onProgress?.Invoke(new FinishedProgress(result));

        try
        {
            await controlConnection.SendControlAsync(
                new GoodbyeMessage(NextRandomId(), new GoodbyeBody { Reason = "test complete" }),
                cancellationToken).ConfigureAwait(false);
        }
        catch { /* cortesía */ }

        try { await Task.WhenAll(persistentReceiveTasks).ConfigureAwait(false); } catch { }

        return result;
    }

    private static PingStats CombinePingStats(PingStats initial, PingStats lossProbe)
        => new PingStats(initial.Samples.Concat(lossProbe.Samples).ToArray());

    private static ulong NextRandomId() =>
        (ulong)Random.Shared.NextInt64(1, long.MaxValue);
}
