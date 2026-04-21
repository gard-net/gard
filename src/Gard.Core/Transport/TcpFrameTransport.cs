using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Gard.Core.Protocol;

namespace Gard.Core.Transport;

/// <summary>
/// Implementación de <see cref="IFrameTransport"/> sobre un <see cref="Socket"/>
/// TCP ya conectado. Agnóstica de plataforma: sólo usa APIs de
/// <c>System.Net.Sockets</c>, disponibles en Windows, macOS, Linux, iOS y
/// Android. El handshake a nivel de aplicación lo hace
/// <c>LandspeedClient</c> / <c>LandspeedListener</c> sobre esta clase.
/// </summary>
public sealed class TcpFrameTransport : IFrameTransport
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;

    public string? RemoteEndpoint { get; }
    public string? LocalEndpoint { get; }

    private TcpFrameTransport(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        RemoteEndpoint = socket.RemoteEndPoint?.ToString();
        LocalEndpoint = socket.LocalEndPoint?.ToString();
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token));
    }

    /// <summary>
    /// Conecta TCP a <paramref name="host"/>:<paramref name="port"/>. Habilita
    /// <c>TCP_NODELAY</c> (equivalente a <c>.noDelay = true</c> en NWProtocolTCP).
    /// </summary>
    public static async Task<TcpFrameTransport> ConnectAsync(
        string host,
        int port,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
            return new TcpFrameTransport(socket);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw LandspeedException.Timeout();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Envuelve un socket ya aceptado por un listener. No vuelve a conectar.
    /// </summary>
    public static TcpFrameTransport FromAccepted(Socket socket)
    {
        socket.NoDelay = true;
        return new TcpFrameTransport(socket);
    }

    public IAsyncEnumerable<Frame> Frames(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask SendAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        var wire = FrameCodec.Encode(frame);
        await SendRawAsync(wire, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendRawAsync(ReadOnlyMemory<byte> wire, CancellationToken cancellationToken = default)
    {
        // Serializa writes: un socket TCP no admite dos WriteAsync simultáneos
        // sin riesgo de intercalar bytes.
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var parser = new FrameStreamParser();
        var buffer = new byte[64 * 1024];
        Exception? terminal = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await _stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex)
                {
                    terminal = LandspeedException.Transport(ex.Message, ex);
                    break;
                }
                catch (SocketException ex)
                {
                    terminal = LandspeedException.Transport(ex.Message, ex);
                    break;
                }

                if (n == 0) break; // EOF remoto

                IReadOnlyList<Frame> frames;
                try
                {
                    frames = parser.Feed(buffer.AsSpan(0, n));
                }
                catch (LandspeedException lx)
                {
                    terminal = lx;
                    break;
                }

                foreach (var f in frames)
                {
                    if (!_incoming.Writer.TryWrite(f))
                    {
                        // Canal unbounded: TryWrite sólo falla si ya está completado.
                        break;
                    }
                }
            }
        }
        finally
        {
            _incoming.Writer.TryComplete(terminal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _readerCts.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { await _readerTask.ConfigureAwait(false); } catch { }
        _stream.Dispose();
        _socket.Dispose();
        _readerCts.Dispose();
        _sendGate.Dispose();
    }
}
