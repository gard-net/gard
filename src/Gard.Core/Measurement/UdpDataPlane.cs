using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Gard.Core.Utils;

namespace Gard.Core.Measurement;

/// <summary>
/// Data-plane UDP (LSP/1.2, spec §12). Standalone respecto a
/// <see cref="IDataPlane"/>: UDP no encaja en el modelo stream-of-frames y
/// se expone como un conjunto de primitivas propias.
///
/// Layout del paquete (spec §12.2):
///   seq    uint64 LE  (offset 0..8)    — 0 = HELLO_UDP, ≥1 = datos
///   ts_ns  uint64 LE  (offset 8..16)   — timestamp monotónico del emisor
///   stream uint16 LE  (offset 16..18)
///   padding N bytes                    — relleno hasta payload_size
/// </summary>
public static class UdpDataPlane
{
    public const int HeaderSize = 18;
    public const int MinPayloadSize = 64;
    public const int MaxPayloadSize = 65_507;
    public const int DefaultPayloadSize = 1200;
    public const int RecvBufferBytes = 4 * 1024 * 1024;
    public const int SendBufferBytes = 2 * 1024 * 1024;

    public static void WriteHeader(Span<byte> dst, ulong seq, ulong tsNs, ushort stream)
    {
        if (dst.Length < HeaderSize)
            throw new ArgumentException($"buffer < {HeaderSize} bytes", nameof(dst));
        BinaryPrimitives.WriteUInt64LittleEndian(dst[..8], seq);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(8, 8), tsNs);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(16, 2), stream);
    }

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out ulong seq, out ulong tsNs, out ushort stream)
    {
        if (data.Length < HeaderSize)
        {
            seq = 0; tsNs = 0; stream = 0;
            return false;
        }
        seq    = BinaryPrimitives.ReadUInt64LittleEndian(data[..8]);
        tsNs   = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8, 8));
        stream = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(16, 2));
        return true;
    }

    /// <summary>Abre N sockets UDP efímeros en el lado host.</summary>
    public static UdpHostSockets HostOpen(int count, IPAddress? bindAddress = null)
    {
        var addr = bindAddress ?? IPAddress.IPv6Any;
        var sockets = new UdpClient[count];
        var ports = new int[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var sock = new UdpClient(addr.AddressFamily);
                if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    sock.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
                }
                sock.Client.ReceiveBufferSize = RecvBufferBytes;
                sock.Client.SendBufferSize = SendBufferBytes;
                sock.Client.Bind(new IPEndPoint(addr, 0));
                sockets[i] = sock;
                ports[i] = ((IPEndPoint)sock.Client.LocalEndPoint!).Port;
            }
            return new UdpHostSockets(sockets, ports);
        }
        catch
        {
            foreach (var s in sockets) { try { s?.Dispose(); } catch { } }
            throw;
        }
    }

    /// <summary>
    /// Espera HELLO_UDP (seq=0) en cada socket host y devuelve el endpoint
    /// remoto aprendido. Cualquier paquete que no sea HELLO se devuelve al
    /// caller via <paramref name="onStrayData"/> para no perder payload si el
    /// cliente ya empezó a enviar (up/bidir). Timeout global en ms.
    /// </summary>
    public static async Task<IPEndPoint[]> HostDrainHelloAsync(
        UdpHostSockets hostSockets,
        int timeoutMs,
        Action<int, UdpReceiveResult>? onStrayData,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var endpoints = new IPEndPoint[hostSockets.Sockets.Count];
        var tasks = new Task[hostSockets.Sockets.Count];
        for (var i = 0; i < hostSockets.Sockets.Count; i++)
        {
            var idx = i;
            var sock = hostSockets.Sockets[i];
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    while (!timeoutCts.IsCancellationRequested)
                    {
                        var r = await sock.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                        if (TryParseHeader(r.Buffer, out var seq, out _, out _) && seq == 0)
                        {
                            endpoints[idx] = r.RemoteEndPoint;
                            return;
                        }
                        onStrayData?.Invoke(idx, r);
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // timeout: endpoints[idx] queda null, reportado abajo
                }
            });
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var missing = new List<int>();
        for (var i = 0; i < endpoints.Length; i++)
            if (endpoints[i] is null) missing.Add(i);
        if (missing.Count > 0)
            throw new TimeoutException(
                $"HELLO_UDP no recibido en {timeoutMs} ms para streams: [{string.Join(",", missing)}]");
        return endpoints;
    }

    /// <summary>Conecta N sockets UDP a los puertos del host y envía HELLO_UDP (seq=0).</summary>
    public static async Task<UdpClientSockets> ClientConnectAsync(
        string host, IReadOnlyList<int> ports, int streamsCount, CancellationToken cancellationToken = default)
    {
        if (ports.Count != streamsCount)
            throw new ArgumentException($"ports.Count ({ports.Count}) != streams ({streamsCount})");

        var sockets = new UdpClient[ports.Count];
        try
        {
            for (var i = 0; i < ports.Count; i++)
            {
                var sock = new UdpClient(AddressFamily.InterNetwork);
                sock.Client.ReceiveBufferSize = RecvBufferBytes;
                sock.Client.SendBufferSize = SendBufferBytes;
                sock.Connect(host, ports[i]);
                sockets[i] = sock;
            }
            // HELLO_UDP (seq=0) por cada socket para abrir pinhole y enseñarle al
            // host la 5-tupla de retorno.
            var helloBuf = new byte[HeaderSize];
            for (var i = 0; i < sockets.Length; i++)
            {
                WriteHeader(helloBuf, seq: 0, tsNs: MonotonicClock.NowNs(), stream: (ushort)i);
                await sockets[i].SendAsync(helloBuf, cancellationToken).ConfigureAwait(false);
            }
            return new UdpClientSockets(sockets);
        }
        catch
        {
            foreach (var s in sockets) { try { s?.Dispose(); } catch { } }
            throw;
        }
    }
}

/// <summary>Sockets UDP del lado host con sus puertos asignados.</summary>
public sealed class UdpHostSockets : IAsyncDisposable
{
    public IReadOnlyList<UdpClient> Sockets { get; }
    public IReadOnlyList<int> Ports { get; }

    internal UdpHostSockets(UdpClient[] sockets, int[] ports)
    {
        Sockets = sockets;
        Ports = ports;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Sockets) { try { s.Dispose(); } catch { } }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>Sockets UDP del lado cliente (uno por stream, connected a un puerto host).</summary>
public sealed class UdpClientSockets : IAsyncDisposable
{
    public IReadOnlyList<UdpClient> Sockets { get; }

    internal UdpClientSockets(UdpClient[] sockets) { Sockets = sockets; }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Sockets) { try { s.Dispose(); } catch { } }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// Estadísticas acumuladas por stream en el receptor UDP. Thread-safe no —
/// cada stream tiene su instancia y es tocada por un único receive loop.
/// </summary>
public sealed class UdpReceiverStats
{
    public ulong PacketsReceived { get; private set; }
    public ulong BytesReceived { get; private set; }
    public ulong MaxSeqSeen { get; private set; }
    public ulong ReorderCount { get; private set; }
    public ulong DuplicateCount { get; private set; }

    /// <summary>Jitter RFC 3550 (EMA de |D_i - D_{i-1}|) en nanosegundos.</summary>
    public double JitterNs { get; private set; }

    // TODO (LSP/1.2): cambiar a bitmap deslizante de 4K entradas para acotar
    // memoria cuando corren tests largos con tasas altas. Por ahora el tracking
    // exacto de duplicados consume O(N) memoria proporcional a paquetes
    // recibidos — aceptable para los duration ≤ 60s de la referencia.
    private readonly HashSet<ulong> _seenSeqs = new();
    private long? _prevTransitNs;

    public void OnPacket(ulong seq, ulong sendTsNs, ulong recvTsNs, int byteCount)
    {
        if (seq == 0) return; // HELLO_UDP; no cuenta como dato.

        if (!_seenSeqs.Add(seq))
        {
            DuplicateCount++;
            return;
        }
        PacketsReceived++;
        BytesReceived += (ulong)byteCount;

        if (seq < MaxSeqSeen) ReorderCount++;
        if (seq > MaxSeqSeen) MaxSeqSeen = seq;

        // RFC 3550 jitter: D = recv - send; J += (|D - prevD| - J) / 16
        var transit = (long)recvTsNs - (long)sendTsNs;
        if (_prevTransitNs is long prev)
        {
            var d = Math.Abs(transit - prev);
            JitterNs += (d - JitterNs) / 16.0;
        }
        _prevTransitNs = transit;
    }
}

/// <summary>
/// Emisor UDP con token bucket opcional para respetar un target bitrate.
/// <paramref name="targetBitrateBps"/> = 0 ⇒ sin límite (rafaga continua).
/// </summary>
public sealed class UdpSender
{
    public ulong PacketsSent { get; private set; }
    public ulong BytesSent { get; private set; }

    public Task RunAsync(
        UdpClient socket,
        ushort streamId,
        int payloadSize,
        ulong targetBitrateBps,
        CancellationToken cancellationToken)
        => RunAsync(socket, target: null, streamId, payloadSize, targetBitrateBps, cancellationToken);

    /// <summary>
    /// <paramref name="target"/> = null ⇒ el socket viene <c>Connect()</c>-ado
    /// (cliente). Si no, se envía vía <c>SendAsync(buf, target)</c> (host
    /// enviando al endpoint aprendido por HELLO_UDP).
    /// </summary>
    public async Task RunAsync(
        UdpClient socket,
        IPEndPoint? target,
        ushort streamId,
        int payloadSize,
        ulong targetBitrateBps,
        CancellationToken cancellationToken)
    {
        var buf = new byte[payloadSize];
        Random.Shared.NextBytes(buf); // padding pseudo-aleatorio, header se reescribe por paquete

        ulong seq = 1;
        // Token bucket: acumula bytes permitidos; cuando < packetSize, duerme.
        double tokensBits = 0;
        var lastTick = MonotonicClock.NowNs();
        var packetBits = (double)payloadSize * 8;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (targetBitrateBps > 0)
            {
                var now = MonotonicClock.NowNs();
                var elapsedS = (now - lastTick) / 1_000_000_000.0;
                lastTick = now;
                tokensBits += elapsedS * targetBitrateBps;
                // Cap el bucket a ~10ms worth para no hacer rafagas gigantes tras pausas.
                var cap = targetBitrateBps * 0.01;
                if (tokensBits > cap) tokensBits = cap;

                if (tokensBits < packetBits)
                {
                    // Calcula cuánto tiempo esperar hasta tener un paquete.
                    var needBits = packetBits - tokensBits;
                    var waitMs = Math.Max(1, (int)Math.Ceiling(needBits / targetBitrateBps * 1000.0));
                    try { await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                tokensBits -= packetBits;
            }

            UdpDataPlane.WriteHeader(buf, seq, MonotonicClock.NowNs(), streamId);
            try
            {
                if (target is null)
                    await socket.SendAsync(buf, cancellationToken).ConfigureAwait(false);
                else
                    await socket.SendAsync(buf, target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { /* peer cerró; seguimos por si se recupera */ continue; }

            PacketsSent++;
            BytesSent += (ulong)payloadSize;
            seq++;
        }
    }
}

/// <summary>Bucle de recepción UDP; invoca OnPacket sobre el collector.</summary>
public static class UdpReceiver
{
    public static async Task RunAsync(
        UdpClient socket,
        UdpReceiverStats stats,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            var recv = MonotonicClock.NowNs();
            var buf = result.Buffer;
            if (!UdpDataPlane.TryParseHeader(buf, out var seq, out var sendTs, out var stream))
                continue;
            stats.OnPacket(seq, sendTs, recv, buf.Length);
        }
    }
}
