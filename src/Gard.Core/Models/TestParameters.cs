using Gard.Core.Protocol;

namespace Gard.Core.Models;

/// <summary>Parámetros con los que se ejecuta un test.</summary>
public sealed record TestParameters
{
    public required TestDirection Direction { get; init; }
    public required double DurationS { get; init; }
    public required int Streams { get; init; }
    public int PayloadSize { get; init; } = 65_536;
    public double WarmupS { get; init; } = 1.0;
    /// <summary>LSP/1.1: relevante sólo si <c>Direction == Bidir</c>.</summary>
    public BidirMode? BidirMode { get; init; }
    /// <summary>LSP/1.1: pausa entre up y down en modo secuencial.</summary>
    public double? GapS { get; init; }

    public TestStartBody AsWireBody() => new()
    {
        Direction = Direction,
        DurationS = DurationS,
        Streams = Streams,
        PayloadSize = PayloadSize,
        WarmupS = WarmupS,
        BidirMode = BidirMode,
        GapS = GapS,
    };
}
