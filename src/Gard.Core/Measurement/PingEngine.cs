using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>Una muestra de ping. <c>RttMs == null</c> ⇒ timeout.</summary>
public sealed record PingSample(uint Seq, double? RttMs);

/// <summary>Resultado agregado de una fase PING (spec §5.4 y §5.6).</summary>
public sealed record PingStats(IReadOnlyList<PingSample> Samples)
{
    public IReadOnlyList<PingSample> Received =>
        Samples.Where(s => s.RttMs.HasValue).ToArray();

    public IReadOnlyList<double> RttMs =>
        Samples.Where(s => s.RttMs.HasValue).Select(s => s.RttMs!.Value).ToArray();

    public double MinMs => RttMs.Count == 0 ? 0 : RttMs.Min();
    public double AvgMs => Statistics.Mean(RttMs);
    public double MaxMs => RttMs.Count == 0 ? 0 : RttMs.Max();
    public double P95Ms => Statistics.Percentile(RttMs, 0.95);
    public double JitterMs => Statistics.Stddev(RttMs);

    public double LossPct
    {
        get
        {
            if (Samples.Count == 0) return 0;
            var missing = Samples.Count(s => s.RttMs is null);
            return 100.0 * missing / Samples.Count;
        }
    }
}

/// <summary>
/// Fase ping/jitter desde el lado cliente. Envía pings secuencialmente y
/// mide RTT contra los pongs reportados por el <see cref="ControlMessageRouter"/>.
/// </summary>
public static class PingEngine
{
    /// <summary>
    /// Envía <paramref name="count"/> pings espaciados <paramref name="intervalMs"/>.
    /// Descarta el primer sample como warmup si <paramref name="discardFirst"/> es true.
    /// </summary>
    public static async Task<PingStats> RunFixedCountAsync(
        IFrameTransport connection,
        ControlMessageRouter router,
        int count,
        int intervalMs = 50,
        int timeoutMs = 500,
        uint startingSeq = 1,
        bool discardFirst = true,
        CancellationToken cancellationToken = default)
    {
        var samples = new List<PingSample>(count);
        for (var i = 0; i < count; i++)
        {
            var seq = unchecked(startingSeq + (uint)i);
            var sample = await SinglePingAsync(connection, router, seq, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            samples.Add(sample);
            if (i < count - 1)
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        IReadOnlyList<PingSample> effective = discardFirst && samples.Count > 0
            ? samples.Skip(1).ToArray()
            : samples.ToArray();
        return new PingStats(effective);
    }

    /// <summary>
    /// Sondeo continuo de pérdida: envía un ping cada <paramref name="intervalMs"/>
    /// hasta que el token se cancele. Pensado para correr durante la fase de throughput.
    /// </summary>
    public static async Task<PingStats> RunLossProbeAsync(
        IFrameTransport connection,
        ControlMessageRouter router,
        int intervalMs = 100,
        int timeoutMs = 500,
        uint startingSeq = 10_000,
        CancellationToken cancellationToken = default)
    {
        var samples = new List<PingSample>();
        var seq = startingSeq;
        while (!cancellationToken.IsCancellationRequested)
        {
            var sample = await SinglePingAsync(connection, router, seq, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            samples.Add(sample);
            seq = unchecked(seq + 1);
            try
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return new PingStats(samples.ToArray());
    }

    private static async Task<PingSample> SinglePingAsync(
        IFrameTransport connection,
        ControlMessageRouter router,
        uint seq,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var sentNs = MonotonicClock.NowNs();
        await connection.SendControlAsync(
            new PingMessage(seq, new PingBody { Seq = seq, SentNs = sentNs }),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await router.AwaitPongAsync(seq, timeoutMs, cancellationToken).ConfigureAwait(false);
            var rttNs = unchecked(MonotonicClock.NowNs() - sentNs);
            return new PingSample(seq, rttNs / 1_000_000.0);
        }
        catch (LandspeedException ex) when (ex.Kind == LandspeedErrorKind.Timeout)
        {
            return new PingSample(seq, null);
        }
    }
}

/// <summary>
/// Lado host: consume pings del router y responde pongs. Termina cuando el
/// stream se cierra o el token se cancela.
/// </summary>
public static class PingResponder
{
    public static async Task RunAsync(
        IFrameTransport connection,
        ControlMessageRouter router,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var (id, body) in router.Pings.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var received = MonotonicClock.NowNs();
                var pong = new PongMessage(id, new PongBody
                {
                    Seq = body.Seq,
                    SentNs = body.SentNs,
                    ReceivedNs = received,
                });
                try
                {
                    await connection.SendControlAsync(pong, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // best effort: si el peer se cayó, salimos del loop abajo
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelación normal
        }
    }
}
