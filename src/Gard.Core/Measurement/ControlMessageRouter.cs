using System.Threading.Channels;
using Gard.Core.Handshake;
using Gard.Core.Protocol;

namespace Gard.Core.Measurement;

/// <summary>
/// Pump de <see cref="ControlMessage"/>s que consume un <see cref="ControlStream"/>
/// y reparte por tipo a consumidores concurrentes. Equivalente al <c>actor
/// ControlMessageRouter</c> Swift: ambos roles (cliente y host) lo usan.
///
/// Mensajes esperados por <c>AwaitX</c> se sirven desde buffer si ya habían
/// llegado antes de que alguien registrara un waiter (evita race al inicio).
/// Streams unbounded (ticks, pings, goodbyes) usan <see cref="Channel"/>.
/// </summary>
public sealed class ControlMessageRouter : IAsyncDisposable
{
    private readonly ControlStream _stream;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private Exception? _terminal;

    // Streams unbounded
    private readonly Channel<TestTickBody> _ticks =
        Channel.CreateUnbounded<TestTickBody>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Channel<(ulong Id, PingBody Body)> _pings =
        Channel.CreateUnbounded<(ulong, PingBody)>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Channel<GoodbyeBody> _goodbyes =
        Channel.CreateUnbounded<GoodbyeBody>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    // --- Cliente ---
    private readonly Dictionary<uint, TaskCompletionSource<PongBody>> _pongWaiters = new();
    private readonly Dictionary<uint, PongBody> _pendingPongs = new();
    private readonly Queue<TaskCompletionSource<(ulong, ResultBody)>> _resultWaiters = new();
    private readonly Queue<(ulong, ResultBody)> _pendingResults = new();
    private readonly Queue<TaskCompletionSource<TestStartAckBody>> _testStartAckWaiters = new();
    private readonly Queue<TestStartAckBody> _pendingTestStartAcks = new();

    // --- Host ---
    private readonly Queue<TaskCompletionSource<(ulong, TestStartBody)>> _testStartWaiters = new();
    private readonly Queue<(ulong, TestStartBody)> _pendingTestStarts = new();
    private readonly Queue<TaskCompletionSource<ulong>> _testEndWaiters = new();
    private readonly Queue<ulong> _pendingTestEnds = new();

    // --- Ambos (LSP/1.2) ---
    private readonly Queue<TaskCompletionSource<UdpStatsReportBody>> _udpStatsWaiters = new();
    private readonly Queue<UdpStatsReportBody> _pendingUdpStats = new();

    public ChannelReader<TestTickBody> Ticks => _ticks.Reader;
    public ChannelReader<(ulong Id, PingBody Body)> Pings => _pings.Reader;
    public ChannelReader<GoodbyeBody> Goodbyes => _goodbyes.Reader;

    public ControlMessageRouter(ControlStream stream)
    {
        _stream = stream;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is not null) return;
            _loopTask = Task.Run(LoopAsync);
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await _stream.NextAsync().ConfigureAwait(false);
                Dispatch(msg);
            }
        }
        catch (Exception ex)
        {
            Terminate(ex);
        }
    }

    private void Dispatch(ControlMessage msg)
    {
        switch (msg)
        {
            case PongMessage pong:
                lock (_gate)
                {
                    if (_pongWaiters.Remove(pong.Body.Seq, out var tcs))
                    {
                        tcs.TrySetResult(pong.Body);
                    }
                    else
                    {
                        _pendingPongs[pong.Body.Seq] = pong.Body;
                    }
                }
                break;

            case TestTickMessage tick:
                _ticks.Writer.TryWrite(tick.Body);
                break;

            case ResultMessage res:
                lock (_gate)
                {
                    if (_resultWaiters.Count > 0)
                    {
                        while (_resultWaiters.Count > 0)
                        {
                            _resultWaiters.Dequeue().TrySetResult((res.Id, res.Body));
                        }
                    }
                    else
                    {
                        _pendingResults.Enqueue((res.Id, res.Body));
                    }
                }
                break;

            case TestStartAckMessage ack:
                lock (_gate)
                {
                    if (_testStartAckWaiters.Count > 0)
                    {
                        while (_testStartAckWaiters.Count > 0)
                        {
                            _testStartAckWaiters.Dequeue().TrySetResult(ack.Body);
                        }
                    }
                    else
                    {
                        _pendingTestStartAcks.Enqueue(ack.Body);
                    }
                }
                break;

            case PingMessage ping:
                _pings.Writer.TryWrite((ping.Id, ping.Body));
                break;

            case TestStartMessage start:
                lock (_gate)
                {
                    if (_testStartWaiters.Count > 0)
                    {
                        while (_testStartWaiters.Count > 0)
                        {
                            _testStartWaiters.Dequeue().TrySetResult((start.Id, start.Body));
                        }
                    }
                    else
                    {
                        _pendingTestStarts.Enqueue((start.Id, start.Body));
                    }
                }
                break;

            case TestEndMessage end:
                lock (_gate)
                {
                    if (_testEndWaiters.Count > 0)
                    {
                        while (_testEndWaiters.Count > 0)
                        {
                            _testEndWaiters.Dequeue().TrySetResult(end.Id);
                        }
                    }
                    else
                    {
                        _pendingTestEnds.Enqueue(end.Id);
                    }
                }
                break;

            case UdpStatsReportMessage stats:
                lock (_gate)
                {
                    if (_udpStatsWaiters.Count > 0)
                    {
                        while (_udpStatsWaiters.Count > 0)
                        {
                            _udpStatsWaiters.Dequeue().TrySetResult(stats.Body);
                        }
                    }
                    else
                    {
                        _pendingUdpStats.Enqueue(stats.Body);
                    }
                }
                break;

            case GoodbyeMessage bye:
                _goodbyes.Writer.TryWrite(bye.Body);
                break;

            case ErrorMessage err:
                Terminate(LandspeedException.Transport(
                    $"peer envió error code={err.Body.Code}: {err.Body.Message}"));
                break;

            default:
                // Mensajes no esperados en esta fase: se ignoran.
                break;
        }
    }

    private void Terminate(Exception ex)
    {
        lock (_gate)
        {
            if (_terminal is not null) return;
            _terminal = ex;

            foreach (var tcs in _pongWaiters.Values) tcs.TrySetException(ex);
            _pongWaiters.Clear();
            while (_resultWaiters.Count > 0) _resultWaiters.Dequeue().TrySetException(ex);
            while (_testStartAckWaiters.Count > 0) _testStartAckWaiters.Dequeue().TrySetException(ex);
            while (_testStartWaiters.Count > 0) _testStartWaiters.Dequeue().TrySetException(ex);
            while (_testEndWaiters.Count > 0) _testEndWaiters.Dequeue().TrySetException(ex);
            while (_udpStatsWaiters.Count > 0) _udpStatsWaiters.Dequeue().TrySetException(ex);
        }
        _ticks.Writer.TryComplete(ex);
        _pings.Writer.TryComplete(ex);
        _goodbyes.Writer.TryComplete(ex);
    }

    // ── API cliente ──────────────────────────────────────────────────────────

    public Task<PongBody> AwaitPongAsync(uint seq, int timeoutMs, CancellationToken ct = default)
    {
        TaskCompletionSource<PongBody> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<PongBody>(_terminal);
            if (_pendingPongs.Remove(seq, out var pending)) return Task.FromResult(pending);
            tcs = new TaskCompletionSource<PongBody>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pongWaiters[seq] = tcs;
        }
        return WithTimeoutAsync(tcs, timeoutMs, () =>
        {
            lock (_gate) _pongWaiters.Remove(seq);
        }, ct);
    }

    public Task<(ulong Id, ResultBody Body)> AwaitResultAsync(int timeoutMs = 30_000, CancellationToken ct = default)
    {
        TaskCompletionSource<(ulong, ResultBody)> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<(ulong, ResultBody)>(_terminal);
            if (_pendingResults.Count > 0) return Task.FromResult(_pendingResults.Dequeue());
            tcs = new TaskCompletionSource<(ulong, ResultBody)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resultWaiters.Enqueue(tcs);
        }
        return WithTimeoutAsync(tcs, timeoutMs, null, ct);
    }

    public Task<TestStartAckBody> AwaitTestStartAckAsync(int timeoutMs = 10_000, CancellationToken ct = default)
    {
        TaskCompletionSource<TestStartAckBody> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<TestStartAckBody>(_terminal);
            if (_pendingTestStartAcks.Count > 0) return Task.FromResult(_pendingTestStartAcks.Dequeue());
            tcs = new TaskCompletionSource<TestStartAckBody>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testStartAckWaiters.Enqueue(tcs);
        }
        return WithTimeoutAsync(tcs, timeoutMs, null, ct);
    }

    // ── API host ─────────────────────────────────────────────────────────────

    public Task<(ulong Id, TestStartBody Body)> AwaitTestStartAsync(int timeoutMs = 30_000, CancellationToken ct = default)
    {
        TaskCompletionSource<(ulong, TestStartBody)> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<(ulong, TestStartBody)>(_terminal);
            if (_pendingTestStarts.Count > 0) return Task.FromResult(_pendingTestStarts.Dequeue());
            tcs = new TaskCompletionSource<(ulong, TestStartBody)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testStartWaiters.Enqueue(tcs);
        }
        return WithTimeoutAsync(tcs, timeoutMs, null, ct);
    }

    public Task<UdpStatsReportBody> AwaitUdpStatsReportAsync(int timeoutMs = 30_000, CancellationToken ct = default)
    {
        TaskCompletionSource<UdpStatsReportBody> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<UdpStatsReportBody>(_terminal);
            if (_pendingUdpStats.Count > 0) return Task.FromResult(_pendingUdpStats.Dequeue());
            tcs = new TaskCompletionSource<UdpStatsReportBody>(TaskCreationOptions.RunContinuationsAsynchronously);
            _udpStatsWaiters.Enqueue(tcs);
        }
        return WithTimeoutAsync(tcs, timeoutMs, null, ct);
    }

    public Task<ulong> AwaitTestEndAsync(int timeoutMs = 60_000, CancellationToken ct = default)
    {
        TaskCompletionSource<ulong> tcs;
        lock (_gate)
        {
            if (_terminal is not null) return Task.FromException<ulong>(_terminal);
            if (_pendingTestEnds.Count > 0) return Task.FromResult(_pendingTestEnds.Dequeue());
            tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testEndWaiters.Enqueue(tcs);
        }
        return WithTimeoutAsync(tcs, timeoutMs, null, ct);
    }

    private static async Task<T> WithTimeoutAsync<T>(
        TaskCompletionSource<T> tcs,
        int timeoutMs,
        Action? onTimeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (tcs.TrySetException(ct.IsCancellationRequested
                ? LandspeedException.Cancelled()
                : LandspeedException.Timeout()))
            {
                onTimeout?.Invoke();
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Terminate(LandspeedException.Cancelled());
        // El loop puede estar bloqueado en _stream.NextAsync() que no conoce
        // nuestro _cts. Disponemos el stream para cerrar su iterador y
        // desbloquear el loop antes de esperar a que termine.
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { /* best effort */ }
        }
        _cts.Dispose();
    }
}
