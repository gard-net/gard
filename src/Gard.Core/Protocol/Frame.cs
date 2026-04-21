namespace Gard.Core.Protocol;

/// <summary>Un frame LSP/1 ya parseado: tipo + payload inmutable.</summary>
public sealed class Frame : IEquatable<Frame>
{
    public FrameType Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public Frame(FrameType type, ReadOnlyMemory<byte> payload = default)
    {
        Type = type;
        Payload = payload;
    }

    /// <summary>Tamaño total del frame en el wire (4 B len + 1 B type + payload).</summary>
    public int WireSize => 4 + 1 + Payload.Length;

    public bool Equals(Frame? other)
    {
        if (other is null) return false;
        if (Type != other.Type) return false;
        return Payload.Span.SequenceEqual(other.Payload.Span);
    }

    public override bool Equals(object? obj) => obj is Frame f && Equals(f);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Type);
        hc.Add(Payload.Length);
        // Hash una muestra acotada de bytes; suficiente para dispersión sin ser O(n).
        var span = Payload.Span;
        var step = Math.Max(1, span.Length / 32);
        for (var i = 0; i < span.Length; i += step) hc.Add(span[i]);
        return hc.ToHashCode();
    }
}
