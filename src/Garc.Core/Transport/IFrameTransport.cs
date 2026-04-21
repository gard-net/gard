using Garc.Core.Protocol;

namespace Garc.Core.Transport;

/// <summary>
/// Abstracción del transporte de frames LSP/1 sobre una conexión TCP viva.
/// Equivalente a <c>LandspeedConnection</c> de la app Apple, pero neutra de
/// plataforma: la implementación de Windows vive en el proyecto UI y usa
/// <c>System.Net.Sockets.TcpClient</c>. Una futura implementación de Android
/// usará <c>Socket</c> nativa de Java/Kotlin vía .NET for Android.
/// </summary>
public interface IFrameTransport : IAsyncDisposable
{
    /// <summary>Flujo de frames entrantes. Se cierra cuando la conexión termina.</summary>
    IAsyncEnumerable<Frame> Frames(CancellationToken cancellationToken = default);

    /// <summary>Serializa y envía un frame. Espera a que el kernel lo acepte.</summary>
    ValueTask SendAsync(Frame frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía bytes ya serializados. Útil en la ruta de throughput caliente para
    /// evitar re-encodear el mismo frame miles de veces.
    /// </summary>
    ValueTask SendRawAsync(ReadOnlyMemory<byte> wire, CancellationToken cancellationToken = default);

    /// <summary>Endpoint remoto resuelto (p. ej. <c>"192.168.1.42:7737"</c>).</summary>
    string? RemoteEndpoint { get; }

    /// <summary>Endpoint local (IP:puerto propio).</summary>
    string? LocalEndpoint { get; }
}

/// <summary>
/// Conveniencia: envía un <see cref="ControlMessage"/> como frame
/// <see cref="FrameType.ControlJson"/>.
/// </summary>
public static class FrameTransportExtensions
{
    public static ValueTask SendControlAsync(
        this IFrameTransport transport,
        ControlMessage message,
        CancellationToken cancellationToken = default)
    {
        var frame = ControlMessageCodec.ToFrame(message);
        return transport.SendAsync(frame, cancellationToken);
    }
}
