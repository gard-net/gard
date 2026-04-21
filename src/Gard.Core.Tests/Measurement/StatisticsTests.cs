using Gard.Core.Measurement;
using Gard.Core.Utils;

namespace Gard.Core.Tests.Measurement;

public class StatisticsTests
{
    [Fact]
    public void Mean_EmptyIsZero()
    {
        Assert.Equal(0.0, Statistics.Mean(Array.Empty<double>()));
    }

    [Fact]
    public void Mean_BasicAverage()
    {
        Assert.Equal(3.0, Statistics.Mean(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }), 9);
    }

    [Fact]
    public void Stddev_SingleSampleIsZero()
    {
        Assert.Equal(0.0, Statistics.Stddev(new[] { 42.0 }));
    }

    [Fact]
    public void Stddev_PopulationFormula()
    {
        // Conjunto clásico {2,4,4,4,5,5,7,9} → stddev poblacional = 2.0.
        var xs = new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 };
        Assert.Equal(2.0, Statistics.Stddev(xs), 9);
    }

    [Fact]
    public void Percentile_EmptyIsZero()
    {
        Assert.Equal(0.0, Statistics.Percentile(Array.Empty<double>(), 0.5));
    }

    [Fact]
    public void Percentile_SingleElementReturnsIt()
    {
        Assert.Equal(7.0, Statistics.Percentile(new[] { 7.0 }, 0.95));
    }

    [Fact]
    public void Percentile_R7Interpolation()
    {
        // 1..10 → p50 = 5.5, p95 = 9.55 (método R-7 / Excel).
        var xs = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        Assert.Equal(5.5, Statistics.Percentile(xs, 0.5), 9);
        Assert.Equal(9.55, Statistics.Percentile(xs, 0.95), 9);
    }

    [Fact]
    public void Percentile_ExactRank()
    {
        // 0..10 (11 elementos) → p50 = 5 (entero exacto, sin interpolación).
        var xs = Enumerable.Range(0, 11).Select(i => (double)i).ToArray();
        Assert.Equal(5.0, Statistics.Percentile(xs, 0.5), 9);
    }

    [Fact]
    public void PingStats_EmptyMetrics()
    {
        var stats = new PingStats(Array.Empty<PingSample>());
        Assert.Equal(0.0, stats.MinMs);
        Assert.Equal(0.0, stats.AvgMs);
        Assert.Equal(0.0, stats.MaxMs);
        Assert.Equal(0.0, stats.JitterMs);
        Assert.Equal(0.0, stats.LossPct);
    }

    [Fact]
    public void PingStats_LossPercent()
    {
        // 4 muestras con 1 timeout ⇒ 25% de pérdida, AvgMs = (10+20+30)/3 = 20.
        var samples = new PingSample[]
        {
            new(1, 10),
            new(2, 20),
            new(3, null),
            new(4, 30),
        };
        var stats = new PingStats(samples);
        Assert.Equal(25.0, stats.LossPct, 9);
        Assert.Equal(20.0, stats.AvgMs, 9);
        Assert.Equal(10.0, stats.MinMs);
        Assert.Equal(30.0, stats.MaxMs);
    }

    [Fact]
    public void PingStats_AllTimeoutLoss100()
    {
        var samples = new PingSample[] { new(1, null), new(2, null) };
        var stats = new PingStats(samples);
        Assert.Equal(100.0, stats.LossPct);
        Assert.Empty(stats.Received);
    }

    [Fact]
    public void ByteCounter_AddAndDrainAreAtomic()
    {
        var counter = new ByteCounter();
        counter.Add(100);
        counter.Add(250);
        Assert.Equal(350UL, counter.Value);
        Assert.Equal(350UL, counter.Drain());
        Assert.Equal(0UL, counter.Value);
    }

    [Fact]
    public void ByteCounter_ResetZeroesValue()
    {
        var counter = new ByteCounter();
        counter.Add(999);
        counter.Reset();
        Assert.Equal(0UL, counter.Value);
    }

    [Fact]
    public async Task ByteCounter_ConcurrentAddsAreExact()
    {
        var counter = new ByteCounter();
        const int workers = 8;
        const int perWorker = 10_000;
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++) counter.Add(1);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal((ulong)(workers * perWorker), counter.Value);
    }
}
