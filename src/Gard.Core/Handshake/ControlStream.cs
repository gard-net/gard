using Gard.Core.Protocol;
using Gard.Core.Transport;

namespace Gard.Core.Handshake;

/// <summary>
/// Lee <see cref="ControlMessage"/>s desde un <see cref="IFrameTransport"/>,
/// descartando frames HEARTBEAT y DATA_*. Mantiene un iterador persistente
/// sobre el stream entrante para no perder frames entre llamadas a <see cref="NextAsync"/>.
/// </summary>
public sealed class ControlStream : IAsyncDisposable
{
    private readonly IAsyncEnumerator<Frame> _iterator;
    private readonly CancellationTokenSource _cts;
    private int _disposed;

    public ControlStream(IFrameTransport transport, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _iterator = transport.Frames(_cts.Token).GetAsyncEnumerator(_cts.Token);
    }

    /// <summary>
    /// Espera el próximo frame CONTROL_JSON y lo decodifica. Descarta frames
    /// distintos a control (heartbeat, data_binary, data_binary_echo). Un frame
    /// ERROR se devuelve como <see cref="ErrorMessage"/> con <c>id = 0</c>.
    /// </summary>
    public async ValueTask<ControlMessage> NextAsync()
    {
        while (await _iterator.MoveNextAsync().ConfigureAwait(false))
        {
            var frame = _iterator.Current;
            switch (frame.Type)
            {
                case FrameType.ControlJson:
                    return ControlMessageCodec.Decode(frame.Payload.Span);
                case FrameType.Error:
                {
                    ErrorBody body;
                    try
                    {
                        body = System.Text.Json.JsonSerializer.Deserialize<ErrorBody>(
                            frame.Payload.Span, ControlMessageCodec.DefaultOptions)
                            ?? new ErrorBody { Code = -1, Message = "error" };
                    }
                    catch
                    {
                        body = new ErrorBody { Code = -1, Message = "error" };
                    }
                    return new ErrorMessage(0, body);
                }
                case FrameType.Heartbeat:
                case FrameType.DataBinary:
                case FrameType.DataBinaryEcho:
                    continue;
            }
        }
        throw LandspeedException.Transport("stream cerrado antes del siguiente mensaje");
    }

    /// <summary>
    /// Espera el próximo mensaje y extrae un cuerpo concreto. Si el peer envía
    /// <c>error</c>, lanza <see cref="LandspeedException"/> con contexto. Si el
    /// mensaje no es del tipo esperado, lanza <c>UnknownControlMessageType</c>.
    /// </summary>
    public async ValueTask<(ulong Id, TBody Body)> ExpectAsync<TBody>(Func<ControlMessage, (ulong, TBody)?> extract)
        where TBody : class
    {
        var msg = await NextAsync().ConfigureAwait(false);
        if (msg is ErrorMessage err)
        {
            throw LandspeedException.Transport(
                $"peer envió error code={err.Body.Code}: {err.Body.Message}");
        }
        var pair = extract(msg);
        if (pair is null)
        {
            throw LandspeedException.UnknownControlMessageType(msg.T);
        }
        return pair.Value;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
        await _iterator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
