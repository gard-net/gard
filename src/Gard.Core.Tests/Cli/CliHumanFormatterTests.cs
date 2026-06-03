using Gard.Cli;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class CliHumanFormatterTests
{
    [Fact]
    public void Format_HasHumanSections()
    {
        var text = CliHumanFormatter.Format(MakeResult());

        Assert.Contains("Gard test result", text);
        Assert.Contains("Throughput", text);
        Assert.Contains("Latency", text);
        Assert.Contains("RTT under load", text);
        Assert.Contains("UDP", text);
    }

    [Theory]
    [InlineData(999UL, "999 b/s")]
    [InlineData(1_500UL, "1.50 Kb/s")]
    [InlineData(2_500_000UL, "2.50 Mb/s")]
    [InlineData(3_500_000_000UL, "3.50 Gb/s")]
    public void FormatBps_ChoosesReadableUnits(ulong bps, string expected)
    {
        Assert.Equal(expected, CliHumanFormatter.FormatBps(bps));
    }

    private static TestResult MakeResult() => new()
    {
        SessionId = "session-1",
        PeerName = "127.0.0.1",
        PeerPlatform = PeerPlatform.Macos,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        Direction = TestDirection.Down,
        Streams = 1,
        DurationS = 1,
        MeanBps = 100_000_000,
        PeakBps = 120_000_000,
        PerStreamBps = [100_000_000],
        PingMinMs = 0.1,
        PingAvgMs = 0.2,
        PingMaxMs = 0.3,
        PingP95Ms = 0.25,
        JitterMs = 0.05,
        LossPct = 0,
        PingSamples = 10,
        RttUnderLoadMs = new RttUnderLoadStats
        {
            Samples = 3,
            MedianMs = 0.2,
            P95Ms = 0.4,
            SpikesCount = 0,
        },
        Udp = new UdpStatsBody
        {
            PacketsSent = 10,
            PacketsReceived = 10,
            PacketsLost = 0,
            LossPct = 0,
            ReorderCount = 0,
            ReorderPct = 0,
            DuplicateCount = 0,
            JitterMs = 0.02,
            TargetBitrateBps = 100_000_000,
            BitrateMissPct = 0,
        },
    };
}
