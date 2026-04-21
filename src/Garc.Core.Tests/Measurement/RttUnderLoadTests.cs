using System.Text.Json;
using Garc.Core.Measurement;
using Garc.Core.Protocol;

namespace Garc.Core.Tests.Measurement;

public class RttUnderLoadTests
{
    [Fact]
    public void Builder_EmptyReturnsEmpty()
    {
        var stats = RttUnderLoadBuilder.FromRttMs(Array.Empty<double>());
        Assert.Equal(0, stats.Samples);
        Assert.Equal(0, stats.SpikesCount);
        Assert.Equal(0.0, stats.MedianMs);
    }

    [Fact]
    public void Builder_BasicStats()
    {
        // 5 muestras regulares: min=10, max=14, median=12.
        var rtt = new[] { 10.0, 11, 12, 13, 14 };
        var stats = RttUnderLoadBuilder.FromRttMs(rtt);
        Assert.Equal(5, stats.Samples);
        Assert.Equal(10.0, stats.MinMs);
        Assert.Equal(14.0, stats.MaxMs);
        Assert.Equal(12.0, stats.MedianMs, 9);
        Assert.Equal(0, stats.SpikesCount);
    }

    [Fact]
    public void Builder_SpikeDetection()
    {
        // Baseline (primer cuartil en orden de llegada) ≈ 10, threshold = 30.
        // 50 y 80 son spikes.
        var rtt = new[] { 10.0, 10, 10, 10, 12, 50, 11, 10, 80, 10 };
        var stats = RttUnderLoadBuilder.FromRttMs(rtt);
        Assert.Equal(10, stats.Samples);
        Assert.Equal(2, stats.SpikesCount);
        Assert.True(stats.BaselineMedianMs > 0);
        Assert.Equal(80.0, stats.MaxMs);
    }

    [Fact]
    public void Collector_AccumulatesThreadSafe()
    {
        var collector = new RttSampleCollector();
        collector.Add(5.0);
        collector.Add(10.0);
        collector.Add(20.0);
        Assert.Equal(3, collector.Count);
        var snap = collector.Snapshot();
        Assert.Equal(3, snap.Samples);
        Assert.Equal(5.0, snap.MinMs);
        Assert.Equal(20.0, snap.MaxMs);
    }

    [Fact]
    public async Task Collector_ConcurrentAdds()
    {
        var collector = new RttSampleCollector();
        const int workers = 4;
        const int perWorker = 1_000;
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++) collector.Add(w + i * 0.1);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(workers * perWorker, collector.Count);
    }

    [Fact]
    public void EchoPayload_TimestampRoundTrip()
    {
        const ulong ts = 123_456_789_012UL;
        var payload = DataChannels.MakeEchoPayload(ts);
        Assert.Equal(DataChannels.EchoPayloadSize, payload.Length);
        var read = DataChannels.ReadEchoTimestampNs(payload);
        Assert.Equal(ts, read);
    }

    [Fact]
    public void EchoPayload_ShortReturnsNull()
    {
        var tooShort = new byte[7];
        Assert.Null(DataChannels.ReadEchoTimestampNs(tooShort));
    }

    [Fact]
    public void EchoPayload_HasRandomTail()
    {
        // Los primeros 8 bytes son determinísticos (timestamp). El resto debe
        // ser aleatorio — dos llamadas con mismo timestamp no deberían producir
        // colas idénticas (colisión ≈ 2^-448).
        var a = DataChannels.MakeEchoPayload(42);
        var b = DataChannels.MakeEchoPayload(42);
        Assert.Equal(a.AsSpan(0, 8).ToArray(), b.AsSpan(0, 8).ToArray());
        Assert.NotEqual(a.AsSpan(8).ToArray(), b.AsSpan(8).ToArray());
    }

    [Fact]
    public void RttUnderLoadStats_JsonSnakeCaseRoundTrip()
    {
        var stats = new RttUnderLoadStats
        {
            Samples = 10,
            MinMs = 1.5,
            MedianMs = 2.5,
            P95Ms = 4.5,
            MaxMs = 5.5,
            StdevMs = 0.75,
            BaselineMedianMs = 2.0,
            SpikesCount = 1,
        };
        var json = JsonSerializer.Serialize(stats, ControlMessageCodec.DefaultOptions);
        Assert.Contains("\"baseline_median_ms\"", json);
        Assert.Contains("\"spikes_count\"", json);
        var back = JsonSerializer.Deserialize<RttUnderLoadStats>(json, ControlMessageCodec.DefaultOptions);
        Assert.Equal(stats, back);
    }
}
