using Gard.Core.Transport;

namespace Gard.Core.Measurement;

/// <summary>
/// Abstracción del plano de datos: apertura de N sockets paralelos entre host y
/// cliente para la fase de throughput. La implementación concreta sobre TCP
/// vive fuera del Core (p. ej. <c>TcpDataPlane</c> en el proyecto Windows).
/// Tests y smoke usan una implementación in-memory/loopback.
/// </summary>
public interface IDataPlane
{
    /// <summary>
    /// Lado host: abre <paramref name="count"/> listeners en puertos efímeros.
    /// Devuelve los puertos listos y un handle que se usará en <see cref="AcceptHostAsync"/>.
    /// No bloquea esperando conexiones.
    /// </summary>
    Task<HostAcceptance> HostOpenAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lado host: espera a aceptar una conexión por listener. Cierra los
    /// listeners una vez aceptadas. Devuelve las conexiones en el mismo orden
    /// de los puertos reportados por <see cref="HostOpenAsync"/>.
    /// </summary>
    Task<IReadOnlyList<IFrameTransport>> AcceptHostAsync(
        HostAcceptance acceptance,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lado cliente: conecta <paramref name="ports"/>.Count sockets a
    /// <paramref name="host"/>. Devuelve las conexiones en el mismo orden.
    /// </summary>
    Task<IReadOnlyList<IFrameTransport>> ClientConnectAsync(
        string host,
        IReadOnlyList<int> ports,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Handle opaco devuelto por <see cref="IDataPlane.HostOpenAsync"/>. Los
/// implementadores guardan los listeners concretos dentro de <see cref="Tag"/>.
/// </summary>
public sealed class HostAcceptance
{
    public required IReadOnlyList<int> Ports { get; init; }
    /// <summary>Payload opaco específico del implementador.</summary>
    public object? Tag { get; init; }
}
