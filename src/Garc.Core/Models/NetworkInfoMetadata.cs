namespace Garc.Core.Models;

/// <summary>
/// Contexto de red capturado client-side al inicio del test. NO viaja por el
/// protocolo; se persiste junto al <see cref="TestResult"/> para que el usuario
/// pueda ver tiempo después desde qué red midió.
/// </summary>
public sealed record NetworkInfoMetadata
{
    public string? LocalIp { get; init; }
    public int? LocalPort { get; init; }
    public string? PeerIp { get; init; }
    public int? PeerPort { get; init; }
    public string? NetworkType { get; init; }
    public string? InterfaceName { get; init; }
    public string? Ssid { get; init; }
    public required string TimezoneIdentifier { get; init; }
    public required int UtcOffsetMinutes { get; init; }
}
