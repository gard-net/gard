using System.Buffers.Binary;
using System.Security.Cryptography;
using Garc.Core.Protocol;
using Garc.Core.Transport;
using Garc.Core.Utils;

namespace Garc.Core.Measurement;

/// <summary>
/// Rol del peer en la fase de datos. Determina el comportamiento al recibir
/// frames <see cref="FrameType.DataBinaryEcho"/>.
/// </summary>
public enum PeerDataRole
{
    /// <summary>Cliente que inicia: recibe echos y calcula RTT.</summary>
    Initiator,
    /// <summary>Host/responder: devuelve los echos al remitente.</summary>
    Responder,
}

/// <summary>
/// Envío y consumo de frames <c>DATA_BINARY</c> sobre sockets ya abiertos.
/// Código agnóstico del transporte: opera a través de <see cref="IFrameTransport"/>.
/// </summary>
public static class DataChannels
{
    /// <summary>64 B: timestamp (8) + padding aleatorio. Ver §5.5.</summary>
    public const int EchoPayloadSize = 64;

    /// <summary>
    /// Envíos concurrentes por stream para saturar la ventana TCP sin inflar
    /// memoria. 4 es el valor de la app Apple.
    /// </summary>
    private const int SendInflightPerStream = 4;

    /// <summary>
    /// Genera un bloque aleatorio de <paramref name="size"/> bytes. La spec §5.5
    /// exige que el tráfico resista compresión por hardware.
    /// </summary>
    public static byte[] MakePayload(int size)
    {
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>Pre-serializa un frame <c>DATA_BINARY</c> de payload fijo.</summary>
    public static byte[] EncodedDataFrame(int size)
    {
        var payload = MakePayload(size);
        return FrameCodec.Encode(new Frame(FrameType.DataBinary, payload));
    }

    /// <summary>
    /// Payload de probe de echo: timestamp monotónico (UInt64 little-endian)
    /// en los primeros 8 bytes, padding aleatorio en los restantes.
    /// </summary>
    public static byte[] MakeEchoPayload(ulong timestampNs)
    {
        var data = new byte[EchoPayloadSize];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0, 8), timestampNs);
        RandomNumberGenerator.Fill(data.AsSpan(8));
        return data;
    }

    /// <summary>Extrae el timestamp del probe de echo. null si el payload es corto.</summary>
    public static ulong? ReadEchoTimestampNs(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return BinaryPrimitives.ReadUInt64LittleEndian(payload[..8]);
    }

    /// <summary>Lado emisor simple: bombea DATA_BINARY hasta cancelación.</summary>
    public static async Task SendLoopAsync(
        IFrameTransport connection,
        int payloadSize,
        ByteCounter counter,
        CancellationToken cancellationToken)
    {
        var wire = EncodedDataFrame(payloadSize);
        while (!cancellationToken.IsCancellationRequested)
        {
            await connection.SendRawAsync(wire, cancellationToken).ConfigureAwait(false);
            counter.Add(payloadSize);
        }
    }

    /// <summary>Lado receptor simple: cuenta bytes de DATA_BINARY/ECHO.</summary>
    public static async Task ReceiveLoopAsync(
        IFrameTransport connection,
        ByteCounter counter,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in connection.Frames(cancellationToken).ConfigureAwait(false))
        {
            switch (frame.Type)
            {
                case FrameType.DataBinary:
                case FrameType.DataBinaryEcho:
                    counter.Add(frame.Payload.Length);
                    break;
            }
            if (cancellationToken.IsCancellationRequested) break;
        }
    }

    /// <summary>
    /// Emisor double-counter: mantiene <see cref="SendInflightPerStream"/>
    /// envíos en vuelo para saturar la ventana TCP. Alimenta el contador por
    /// stream y el global.
    /// </summary>
    public static async Task SendLoopDoubleAsync(
        IFrameTransport connection,
        int payloadSize,
        ByteCounter counter,
        ByteCounter global,
        CancellationToken cancellationToken)
    {
        var wire = EncodedDataFrame(payloadSize);
        var workers = new Task[SendInflightPerStream];
        for (var w = 0; w < SendInflightPerStream; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await connection.SendRawAsync(wire, cancellationToken).ConfigureAwait(false);
                        counter.Add(payloadSize);
                        global.Add(payloadSize);
                    }
                    catch (OperationCanceledException) { return; }
                    catch when (cancellationToken.IsCancellationRequested) { return; }
                }
            }, cancellationToken);
        }
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    /// <summary>
    /// Receptor double-counter role-aware. Soporta LSP/1.1
    /// <c>DATA_BINARY_ECHO</c>: responder reenvía el payload; initiator extrae
    /// timestamp y lo reporta a <paramref name="echoCollector"/>.
    /// Los frames <c>DATA_BINARY</c> (0x02) siempre cuentan como throughput.
    /// </summary>
    public static async Task ReceiveLoopDoubleAsync(
        IFrameTransport connection,
        ByteCounter counter,
        ByteCounter global,
        PeerDataRole role,
        RttSampleCollector? echoCollector,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in connection.Frames(cancellationToken).ConfigureAwait(false))
            {
                switch (frame.Type)
                {
                    case FrameType.DataBinary:
                        counter.Add(frame.Payload.Length);
                        global.Add(frame.Payload.Length);
                        break;

                    case FrameType.DataBinaryEcho:
                        if (role == PeerDataRole.Responder)
                        {
                            var echoed = FrameCodec.Encode(
                                new Frame(FrameType.DataBinaryEcho, frame.Payload));
                            // Fire-and-forget para no bloquear la recepción del siguiente frame.
                            _ = Task.Run(async () =>
                            {
                                try { await connection.SendRawAsync(echoed, cancellationToken).ConfigureAwait(false); }
                                catch { /* best effort */ }
                            }, cancellationToken);
                        }
                        else if (echoCollector is not null)
                        {
                            var sentNs = ReadEchoTimestampNs(frame.Payload.Span);
                            if (sentNs is ulong sent)
                            {
                                var now = MonotonicClock.NowNs();
                                if (now > sent)
                                {
                                    var rttNs = now - sent;
                                    echoCollector.Add(rttNs / 1_000_000.0);
                                }
                            }
                        }
                        break;
                }
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    /// <summary>
    /// Lado cliente: inyecta probes de echo en paralelo al tráfico DATA_BINARY.
    /// Errores de envío se descartan silenciosamente — un probe perdido no aborta
    /// el test.
    /// </summary>
    public static async Task EchoProbeLoopAsync(
        IFrameTransport connection,
        int intervalMs = 100,
        CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, intervalMs));
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = MakeEchoPayload(MonotonicClock.NowNs());
            try
            {
                var frame = new Frame(FrameType.DataBinaryEcho, payload);
                var wire = FrameCodec.Encode(frame);
                await connection.SendRawAsync(wire, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* send lost — no abortar */ }

            try { await Task.Delay(interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
