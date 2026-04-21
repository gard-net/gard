using System.Text.Json;
using Garc.Core.Measurement;
using Garc.Core.Protocol;

namespace Garc.Core.Tests.Measurement;

public class IntervalReportBuilderTests
{
    [Fact]
    public void Empty_GivesZeroStats()
    {
        var report = IntervalReportBuilder.FromSamples(200, Array.Empty<IntervalSample>());
        Assert.Equal(200, report.WindowMs);
        Assert.Empty(report.Samples);
        Assert.Equal(0UL, report.Stats.MedianBps);
        Assert.Equal(0UL, report.Stats.P95Bps);
        Assert.Equal(0UL, report.Stats.MinBps);
        Assert.Equal(0UL, report.Stats.MaxBps);
    }

    [Fact]
    public void Stats_ComputedFromSamples()
    {
        // 5 muestras 100M..140M en orden de llegada.
        var samples = new IntervalSample[]
        {
            new() { StartS = 0.0, EndS = 0.2, Bps = 100_000_000 },
            new() { StartS = 0.2, EndS = 0.4, Bps = 110_000_000 },
            new() { StartS = 0.4, EndS = 0.6, Bps = 120_000_000 },
            new() { StartS = 0.6, EndS = 0.8, Bps = 130_000_000 },
            new() { StartS = 0.8, EndS = 1.0, Bps = 140_000_000 },
        };
        var report = IntervalReportBuilder.FromSamples(200, samples);
        Assert.Equal(5, report.Samples.Count);
        Assert.Equal(120_000_000UL, report.Stats.MedianBps);
        Assert.Equal(100_000_000UL, report.Stats.MinBps);
        Assert.Equal(140_000_000UL, report.Stats.MaxBps);
        // R-7 p95 de [100M..140M] = 138M.
        Assert.Equal(138_000_000UL, report.Stats.P95Bps);
    }

    [Fact]
    public void JsonRoundTrip_SnakeCase()
    {
        var samples = new IntervalSample[]
        {
            new() { StartS = 0.0, EndS = 0.2, Bps = 100_000_000 },
            new() { StartS = 0.2, EndS = 0.4, Bps = 110_000_000 },
        };
        var report = IntervalReportBuilder.FromSamples(200, samples);
        var json = JsonSerializer.Serialize(report, ControlMessageCodec.DefaultOptions);
        Assert.Contains("\"window_ms\"", json);
        Assert.Contains("\"start_s\"", json);
        Assert.Contains("\"median_bps\"", json);
        Assert.Contains("\"p95_bps\"", json);

        var back = JsonSerializer.Deserialize<IntervalReport>(json, ControlMessageCodec.DefaultOptions);
        Assert.NotNull(back);
        Assert.Equal(report.WindowMs, back!.WindowMs);
        Assert.Equal(report.Stats.MedianBps, back.Stats.MedianBps);
        Assert.Equal(report.Samples.Count, back.Samples.Count);
    }

    [Fact]
    public void ResultBody_OmitsIntervalsWhenNull()
    {
        var body = new ResultBody
        {
            SessionId = "s1",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
            Direction = TestDirection.Down,
            Streams = 4,
            DurationS = 10.0,
            Throughput = new ThroughputBody
            {
                MeanBps = 1_000_000,
                PeakBps = 1_500_000,
                PerStreamBps = new ulong[] { 250_000, 250_000, 250_000, 250_000 },
            },
            LatencyMs = new LatencyBody { Min = 1, Avg = 2, Max = 5, P95 = 4 },
            JitterMs = 0.3,
            LossPct = 0,
            Samples = 20,
            ProtocolVersion = ProtocolVersion.Current,
            // Intervals, RttUnderLoad, ThroughputUp, ThroughputDown quedan null.
        };

        var wire = ControlMessageCodec.Encode(new ResultMessage(42, body));
        var json = System.Text.Encoding.UTF8.GetString(wire);

        Assert.DoesNotContain("\"intervals\"", json);
        Assert.DoesNotContain("\"rtt_under_load\"", json);
        Assert.DoesNotContain("\"throughput_up\"", json);
        Assert.DoesNotContain("\"throughput_down\"", json);
    }

    [Fact]
    public void ResultBody_IncludesIntervalsWhenPresent()
    {
        var intervals = IntervalReportBuilder.FromSamples(200, new IntervalSample[]
        {
            new() { StartS = 0.0, EndS = 0.2, Bps = 100_000_000 },
        });
        var body = new ResultBody
        {
            SessionId = "s1",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
            Direction = TestDirection.Down,
            Streams = 1,
            DurationS = 0.2,
            Throughput = new ThroughputBody
            {
                MeanBps = 100_000_000,
                PeakBps = 100_000_000,
                PerStreamBps = new ulong[] { 100_000_000 },
            },
            LatencyMs = new LatencyBody { Min = 0, Avg = 0, Max = 0, P95 = 0 },
            JitterMs = 0,
            LossPct = 0,
            Samples = 0,
            ProtocolVersion = ProtocolVersion.Current,
            Intervals = intervals,
        };

        var wire = ControlMessageCodec.Encode(new ResultMessage(7, body));
        var json = System.Text.Encoding.UTF8.GetString(wire);
        Assert.Contains("\"intervals\"", json);
        Assert.Contains("\"window_ms\":200", json);
    }
}
