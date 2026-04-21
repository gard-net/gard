using Garc.Core.Models;

namespace Garc.Core.Persistence;

/// <summary>
/// Preferencias de la app persistidas entre sesiones. Equivalente al bundle
/// <c>Preferences</c> Swift, pero expresado como record inmutable: el store
/// emite copias nuevas en cada actualización.
/// </summary>
public sealed record Preferences
{
    /// <summary>Unidad de throughput mostrada en UI. Default: <see cref="ThroughputUnit.Mbps"/>.</summary>
    public ThroughputUnit ThroughputUnit { get; init; } = ThroughputUnit.Mbps;

    /// <summary>Número de streams usado por defecto al iniciar un test. Rango válido: [1, 16].</summary>
    public int DefaultStreams { get; init; } = 4;

    /// <summary>Duración por defecto de un test, en segundos. Rango válido: [1.0, 60.0].</summary>
    public double DefaultDurationS { get; init; } = 10.0;

    /// <summary>Si el usuario ya vio el onboarding inicial.</summary>
    public bool HasSeenOnboarding { get; init; } = false;

    /// <summary>Preferencia de apariencia. Default: <see cref="AppearanceMode.System"/>.</summary>
    public AppearanceMode Appearance { get; init; } = AppearanceMode.System;

    public const int MinStreams = 1;
    public const int MaxStreams = 16;
    public const double MinDurationS = 1.0;
    public const double MaxDurationS = 60.0;

    /// <summary>
    /// Devuelve una copia con todos los campos dentro de los rangos permitidos.
    /// Los enums inválidos (p.ej. valor fuera del enum tras una edición manual
    /// del JSON) se reemplazan por el default. Llamar antes de persistir y
    /// después de leer.
    /// </summary>
    public Preferences Sanitized() => this with
    {
        DefaultStreams = Math.Clamp(DefaultStreams, MinStreams, MaxStreams),
        DefaultDurationS = Math.Clamp(
            double.IsFinite(DefaultDurationS) ? DefaultDurationS : 10.0,
            MinDurationS, MaxDurationS),
        ThroughputUnit = Enum.IsDefined(ThroughputUnit) ? ThroughputUnit : ThroughputUnit.Mbps,
        Appearance = Enum.IsDefined(Appearance) ? Appearance : AppearanceMode.System,
    };
}
