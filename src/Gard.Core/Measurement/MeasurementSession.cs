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
    /// <param name="PerStreamBps">Recibido por stream (en bidir: la bajada).</param>
    /// <param name="Split">Sólo en bidir simultáneo: subida (enviado) y bajada (recibido) por separado.</param>
    private sealed record PhaseSnapshot(
        ulong MeanBps,
        ulong PeakBps,
        IReadOnlyList<ulong> PerStreamBps,
        ulong TotalBytes,
        double DurS,
        IReadOnlyList<IntervalSample> IntervalSamples,
        DirectionSplit? Split = null);

    private sealed record DirectionSplit(
        ulong DownMeanBps, ulong DownPeakBps,
        ulong UpMeanBps, ulong UpPeakBps, IReadOnlyList<ulong> UpPerStreamBps);

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
        EnsureSupportedByPeer(parms, inputs.NegotiatedCaps);

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
            throw LandspeedException.TestRejected(ack.Reason ?? "sin motivo");
        }
        if (ack.DataPorts.Count != parms.Streams)
        {
            throw LandspeedException.Transport(
                $"host devolvió {ack.DataPorts.Count} puertos, esperados {parms.Streams}");
        }

        // LSP/1.2: si el test es UDP, delega al orquestador UDP (no abre TCP).
        if (parms.Transport == TestTransport.Udp)
        {
            return await UdpMeasurement.RunClientAsync(
                controlConnection, router, remoteHost, ack.DataPorts,
                inputs, pingStats, startedAt, onProgress,
                cancellationToken).ConfigureAwait(false);
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
        // Se habilita sólo dentro de cada ventana de medición (post-warmup).
        var rttCollector = echoEnabled ? new RttSampleCollector { Accepting = false } : null;
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

            // En bidir simultáneo lo enviado va a contadores propios: mezclado con lo
            // recibido no había forma de reportar subida y bajada por separado.
            var bidir = phaseDirection == TestDirection.Bidir;
            var sendCounters = bidir ? Enumerable.Range(0, dataConns.Count).Select(_ => new ByteCounter()).ToArray() : null;
            var sendGlobal = bidir ? new ByteCounter() : globalCounter;

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTasks = new List<Task>();
            if (phaseDirection == TestDirection.Up || bidir)
            {
                for (var i = 0; i < dataConns.Count; i++)
                {
                    var sendCnt = sendCounters?[i] ?? counters[i];
                    var conn = dataConns[i];
                    sendTasks.Add(DataChannels.SendLoopDoubleAsync(
                        conn, parms.PayloadSize, sendCnt, sendGlobal, sendCts.Token));
                }
            }

            // Warmup.
            if (parms.WarmupS > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(parms.WarmupS), cancellationToken).ConfigureAwait(false);
                _ = globalCounter.Drain();
                _ = sendGlobal.Drain();
                foreach (var c in counters) c.Reset();
                if (sendCounters is not null) foreach (var c in sendCounters) c.Reset();
            }

            var samples = new List<IntervalSample>();
            if (rttCollector is not null) rttCollector.Accepting = true;
            var phaseStart = MonotonicClock.NowNs();
            var phaseEnd = phaseStart + (ulong)(parms.DurationS * 1_000_000_000);
            ulong phasePeakBps = 0, downPeakBps = 0, upPeakBps = 0;
            ulong measuredBytesTotal = 0, sentBytesTotal = 0;
            var windowStart = phaseStart;
            var nextWindow = phaseStart + WindowNs;
            while (MonotonicClock.NowNs() < phaseEnd)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                var now = MonotonicClock.NowNs();
                if (now >= nextWindow)
                {
                    var receivedBytes = globalCounter.Drain();
                    var sentBytes = bidir ? sendGlobal.Drain() : 0;
                    var windowBytes = receivedBytes + sentBytes;
                    measuredBytesTotal += windowBytes;
                    sentBytesTotal += sentBytes;
                    // Ventana real, no 200 ms fijos (`bytes * 40` como Swift): un tick
                    // atrasado por GC/scheduling inflaba el bps y sesgaba peak_bps.
                    var elapsedNs = Math.Max(1UL, now - windowStart);
                    ulong Bps(ulong bytes) => (ulong)(bytes * 8.0 * 1_000_000_000.0 / elapsedNs);
                    var bps = Bps(windowBytes);
                    if (bps > phasePeakBps) phasePeakBps = bps;
                    downPeakBps = Math.Max(downPeakBps, Bps(receivedBytes));
                    upPeakBps = Math.Max(upPeakBps, Bps(sentBytes));
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
            var tailSent = bidir ? sendGlobal.Drain() : 0;
            measuredBytesTotal += globalCounter.Drain() + tailSent;
            sentBytesTotal += tailSent;
            if (rttCollector is not null) rttCollector.Accepting = false;

            var phaseDurS = Math.Max(
                (MonotonicClock.NowNs() - phaseStart) / 1_000_000_000.0, 0.001);
            ulong Mean(ulong bytes) => (ulong)(bytes * 8.0 / phaseDurS);
            var phaseMeanBps = Mean(measuredBytesTotal);
            var phasePerStreamBps = counters.Select(c => Mean(c.Value)).ToArray();
            var split = sendCounters is null ? null : new DirectionSplit(
                DownMeanBps: Mean(measuredBytesTotal - sentBytesTotal), DownPeakBps: downPeakBps,
                UpMeanBps: Mean(sentBytesTotal), UpPeakBps: upPeakBps,
                UpPerStreamBps: sendCounters.Select(c => Mean(c.Value)).ToArray());

            sendCts.Cancel();
            try { await Task.WhenAll(sendTasks).ConfigureAwait(false); } catch { }

            return new PhaseSnapshot(
                phaseMeanBps, phasePeakBps, phasePerStreamBps,
                measuredBytesTotal, phaseDurS, samples, split);
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
                    // Simultáneo: bajada = lo que recibió el cliente. Subida = lo que
                    // recibió el host por stream (receptor, más fiel que lo aceptado
                    // por el kernel del emisor); si el host no lo reporta así, se usa
                    // lo enviado por el cliente.
                    var snap = phaseSnapshots[0];
                    var split = snap.Split!;
                    var hostPerStream = hostResult.Throughput.PerStreamBps;
                    var upPerStream = hostPerStream.Count == parms.Streams && hostPerStream.Any(b => b > 0)
                        ? hostPerStream
                        : split.UpPerStreamBps;
                    var upMean = upPerStream.Aggregate(0UL, (a, b) => a + b);
                    finalThroughputUp = new ThroughputBody
                    {
                        MeanBps = upMean,
                        PeakBps = Math.Max(split.UpPeakBps, upMean),
                        PerStreamBps = upPerStream,
                    };
                    finalThroughputDown = new ThroughputBody
                    {
                        MeanBps = split.DownMeanBps,
                        PeakBps = Math.Max(split.DownPeakBps, split.DownMeanBps),
                        PerStreamBps = snap.PerStreamBps,
                    };
                    finalMeanBps = upMean + split.DownMeanBps;
                    finalPeakBps = Math.Max(peakBps, finalMeanBps);
                    finalPerStreamBps = snap.PerStreamBps.Zip(upPerStream, (d, u) => d + u).ToArray();
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

    /// <summary>
    /// Falla antes de la fase de ping si el test pide algo que el peer no anunció.
    /// Un host LSP/1.1 que recibe <c>transport: "udp"</c> no lo entiende y corta la
    /// conexión, y el usuario veía "stream cerrado" sin saber por qué. El bidir
    /// secuencial sin <c>bidir_sequential</c> antes degradaba en silencio a
    /// simultáneo, midiendo otra cosa que la pedida.
    /// </summary>
    public static void EnsureSupportedByPeer(TestParameters parms, Capabilities negotiated)
    {
        if (parms.Transport == TestTransport.Udp && !negotiated.HasFlag(Capabilities.UdpDataPlane))
            throw LandspeedException.UdpNotNegotiated();
        if (parms.Direction == TestDirection.Bidir && parms.BidirMode == BidirMode.Sequential
            && !negotiated.HasFlag(Capabilities.BidirSequential))
            throw LandspeedException.TestRejected("el peer no soporta bidir secuencial (bidir_sequential)");
    }

    private static ulong NextRandomId() =>
        (ulong)Random.Shared.NextInt64(1, long.MaxValue);
}
