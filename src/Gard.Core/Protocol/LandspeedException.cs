namespace Gard.Core.Protocol;

/// <summary>
/// Error del protocolo o del núcleo. Cada caso tiene un código estable (ver
/// <c>docs/LANDSPEED_PROTOCOL_v1.md</c> §8).
/// </summary>
public sealed class LandspeedException : Exception
{
    public LandspeedErrorKind Kind { get; }

    /// <summary>Datos contextuales del error (frame len, tipo desconocido, etc.).</summary>
    public IReadOnlyDictionary<string, object?> Context { get; }

    /// <summary>
    /// Código numérico transmitido en frames ERROR (0xFF) y documentado en la spec.
    /// <c>null</c> para errores que no viajan por el wire (cancelled, timeout, transport…).
    /// </summary>
    public int? WireCode => Kind switch
    {
        LandspeedErrorKind.FrameTooLarge               => 1001,
        LandspeedErrorKind.FrameLengthZero             => 1002,
        LandspeedErrorKind.UnknownFrameType            => 1003,
        LandspeedErrorKind.MalformedControlJson        => 1004,
        LandspeedErrorKind.UnknownControlMessageType   => 1005,
        LandspeedErrorKind.IncompatibleProtocolVersion => 2000,
        LandspeedErrorKind.PairingInvalid              => 3001,
        LandspeedErrorKind.PairingRequired             => 3002,
        _ => null,
    };

    private LandspeedException(LandspeedErrorKind kind, string message, IReadOnlyDictionary<string, object?> context, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Context = context;
    }

    public static LandspeedException FrameTooLarge(uint declaredLength, int limit) =>
        new(LandspeedErrorKind.FrameTooLarge,
            $"Frame demasiado grande: {declaredLength} B (límite {limit} B).",
            new Dictionary<string, object?> { ["declaredLength"] = declaredLength, ["limit"] = limit });

    public static LandspeedException FrameLengthZero() =>
        new(LandspeedErrorKind.FrameLengthZero,
            "Longitud de frame 0; se requiere al menos el byte de tipo.",
            new Dictionary<string, object?>());

    public static LandspeedException UnknownFrameType(byte type) =>
        new(LandspeedErrorKind.UnknownFrameType,
            $"Tipo de frame desconocido: 0x{type:X}.",
            new Dictionary<string, object?> { ["type"] = type });

    public static LandspeedException MalformedControlJson(string underlying, Exception? inner = null) =>
        new(LandspeedErrorKind.MalformedControlJson,
            $"JSON de control inválido: {underlying}",
            new Dictionary<string, object?> { ["underlying"] = underlying }, inner);

    public static LandspeedException UnknownControlMessageType(string t) =>
        new(LandspeedErrorKind.UnknownControlMessageType,
            $"Tipo de mensaje de control desconocido: \"{t}\".",
            new Dictionary<string, object?> { ["t"] = t });

    public static LandspeedException IncompatibleProtocolVersion(string peerVersion) =>
        new(LandspeedErrorKind.IncompatibleProtocolVersion,
            $"Versión de protocolo incompatible (peer: {peerVersion}).",
            new Dictionary<string, object?> { ["peerVersion"] = peerVersion });

    public static LandspeedException PairingInvalid() =>
        new(LandspeedErrorKind.PairingInvalid, "Código de emparejamiento inválido.",
            new Dictionary<string, object?>());

    public static LandspeedException PairingRequired() =>
        new(LandspeedErrorKind.PairingRequired, "Se requiere emparejamiento antes de esta operación.",
            new Dictionary<string, object?>());

    public static LandspeedException Cancelled() =>
        new(LandspeedErrorKind.Cancelled, "Operación cancelada.", new Dictionary<string, object?>());

    public static LandspeedException Timeout() =>
        new(LandspeedErrorKind.Timeout, "Tiempo de espera agotado.", new Dictionary<string, object?>());

    public static LandspeedException Transport(string description, Exception? inner = null) =>
        new(LandspeedErrorKind.Transport, $"Error de transporte: {description}",
            new Dictionary<string, object?> { ["description"] = description }, inner);

    public static LandspeedException InternalInconsistency(string description) =>
        new(LandspeedErrorKind.InternalInconsistency, $"Inconsistencia interna: {description}",
            new Dictionary<string, object?> { ["description"] = description });
}

public enum LandspeedErrorKind
{
    FrameTooLarge,
    FrameLengthZero,
    UnknownFrameType,
    MalformedControlJson,
    UnknownControlMessageType,
    IncompatibleProtocolVersion,
    PairingInvalid,
    PairingRequired,
    Cancelled,
    Timeout,
    Transport,
    InternalInconsistency,
}
