namespace Gard.Core.Measurement;

/// <summary>
/// Contador thread-safe de bytes, usado por los data channels durante la
/// medición de throughput. Soporta <see cref="Drain"/> atómico para muestreo
/// periódico cada ~200 ms.
/// </summary>
public sealed class ByteCounter
{
    private long _value;

    public ulong Value => (ulong)Interlocked.Read(ref _value);

    public void Add(int n) => Interlocked.Add(ref _value, n);

    public void Add(long n) => Interlocked.Add(ref _value, n);

    public void Reset() => Interlocked.Exchange(ref _value, 0);

    /// <summary>Snapshot + reset atómico.</summary>
    public ulong Drain() => (ulong)Interlocked.Exchange(ref _value, 0);
}
