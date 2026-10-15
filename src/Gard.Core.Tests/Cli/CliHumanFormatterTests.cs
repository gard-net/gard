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

    private static CliTerminal Rich(bool color = false, int width = 80, bool unicode = true) =>
        new(new CliStyleOptions(Plain: false, NoColor: !color, Style: "rich"),
            forceInteractive: true, forceWidth: width, forceUnicode: unicode);

    private static string[] PanelLines(string text) =>
        text.Split(Environment.NewLine).Select(CliText.StripAnsi).ToArray();

    [Fact]
    public void FormatRich_RendersPanelWithAllSections()
    {
        var text = CliHumanFormatter.Format(MakeResult(), Rich(color: true));
        var plain = CliText.StripAnsi(text);

        Assert.StartsWith("╭─ gard", plain);
        Assert.Contains("╰", plain);
        foreach (var section in new[] { "Throughput", "Latency", "Jitter", "Probe loss", "Under load", "Datagrams", "Target" })
            Assert.Contains(section, plain);
        Assert.Contains("100.00 Mb/s", plain);
        Assert.Contains("mean", plain);
        Assert.Contains("peak", plain);
        Assert.Contains("good", plain);
        Assert.Contains("✓", plain);
    }

    [Fact]
    public void FormatRich_ShowsUpDownLanesWhenBidirectional()
    {
        var result = MakeResult() with
        {
            ThroughputUp = new ThroughputBody { MeanBps = 40_000_000, PeakBps = 50_000_000, PerStreamBps = [40_000_000] },
            ThroughputDown = new ThroughputBody { MeanBps = 90_000_000, PeakBps = 110_000_000, PerStreamBps = [90_000_000] },
        };

        var text = CliHumanFormatter.Format(result, Rich());

        Assert.Contains("↑ up", text);
        Assert.Contains("↓ down", text);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(80, 80)]
    [InlineData(200, 84)]  // tope: un panel de 200 columnas se lee peor
    [InlineData(40, 56)]   // mínimo legible
    public void FormatRich_AdaptsToTerminalWidth_AndKeepsLinesAligned(int termWidth, int expected)
    {
        var lines = PanelLines(CliHumanFormatter.Format(MakeResult(), Rich(color: true, width: termWidth)));

        Assert.All(lines, l => Assert.Equal(expected, CliText.VisibleLength(l)));
    }

    [Fact]
    public void FormatRich_AsciiMode_UsesOnlyAscii()
    {
        var text = CliHumanFormatter.Format(MakeResult(), Rich(unicode: false));

        Assert.All(text, c => Assert.True(c < 128, $"no-ASCII: U+{(int)c:X4}"));
        Assert.StartsWith("+- gard", text);
        Assert.Contains("| ", text);
    }

    [Fact]
    public void FormatRich_DrawsSparklineFromIntervals()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(i => new IntervalSample { StartS = i * 0.2, EndS = (i + 1) * 0.2, Bps = (ulong)(50_000_000 + i * 5_000_000) })
            .ToArray();
        var result = MakeResult() with
        {
            Intervals = new IntervalReport
            {
                WindowMs = 200,
                Samples = samples,
                Stats = new IntervalStats { MedianBps = 1, P95Bps = 1, StdevBps = 0, MinBps = 1, MaxBps = 1 },
            },
        };

        var text = CliHumanFormatter.Format(result, Rich());

        Assert.Contains("▁", text);
        Assert.Contains("█", text);
    }

    [Fact]
    public void FormatRich_KeepsAnsiOutWhenNoColor()
    {
        var text = CliHumanFormatter.Format(MakeResult(), Rich(color: false));

        Assert.Contains("Throughput", text);
        Assert.DoesNotContain("\u001b[", text);
    }

    [Fact]
    public void FormatRich_ColorModeKeepsValidAnsiTerminators()
    {
        var text = CliHumanFormatter.Format(MakeResult(), Rich(color: true));

        Assert.Contains("\u001b[", text);
        Assert.DoesNotContain("\u001b[0M", text);
        Assert.Contains("\u001b[0m", text);
    }

    [Theory]
    [InlineData(0.0, 10)]
    [InlineData(0.5, 10)]
    [InlineData(1.0, 10)]
    [InlineData(double.NaN, 10)]
    public void Gauge_HasExactWidthAndProportionalFill(double ratio, int width)
    {
        var gauge = CliTable.Gauge(ratio, width);

        Assert.Equal(width, gauge.Length);
        var full = gauge.Count(c => c == '█');
        if (ratio >= 1.0) Assert.Equal(width, full);
        else if (ratio == 0.5) Assert.Equal(width / 2, full);
        else Assert.Equal(0, full);
    }

    [Fact]
    public void Terminal_DisablesRichWhenPlain()
    {
        var term = new CliTerminal(new CliStyleOptions(Plain: true, NoColor: false, Style: "rich"), forceInteractive: true);

        Assert.False(term.Rich);
        Assert.False(term.Color);
    }

    [Theory]
    [InlineData("NO_COLOR", "1")]
    [InlineData("TERM", "dumb")]
    [InlineData("CI", "true")]
    public void Terminal_HonorsEnvironmentForDefaultDetection(string name, string value)
    {
        var old = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            var term = new CliTerminal(CliStyleOptions.Default);
            if (name == "NO_COLOR") Assert.False(term.Color);
            else Assert.False(term.Rich);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, old);
        }
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
