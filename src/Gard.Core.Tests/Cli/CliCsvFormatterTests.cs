using Gard.Cli;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class CliCsvFormatterTests
{
    [Fact]
    public void Header_MatchesExpectedColumnCount()
    {
        Assert.Equal(32, CliCsvFormatter.Columns.Length);
        Assert.StartsWith("label,direction,streams", CliCsvFormatter.Header());
    }

    [Fact]
    public void Escape_QuotesCommasQuotesAndNewlines()
    {
        Assert.Equal("simple", CliCsvFormatter.Escape("simple"));
        Assert.Equal("\"a,b\"", CliCsvFormatter.Escape("a,b"));
        Assert.Equal("\"a\"\"b\"", CliCsvFormatter.Escape("a\"b"));
        Assert.Equal("\"a\nb\"", CliCsvFormatter.Escape("a\nb"));
    }

    [Fact]
    public void Format_EscapesLabelForMachineReadableCsv()
    {
        var result = MakeResult();
        var parameters = new TestParameters
        {
            Direction = TestDirection.Down,
            DurationS = 10,
            Streams = 1,
            PayloadSize = 65_536,
            WarmupS = 1,
            Transport = TestTransport.Tcp,
        };

        var row = CliCsvFormatter.Format(result, parameters, "run,\"quoted\"");

        Assert.StartsWith("\"run,\"\"quoted\"\"\",down,1,10.00", row);
    }

    private static TestResult MakeResult() => new()
    {
        SessionId = "s",
        PeerName = "peer",
        PeerPlatform = PeerPlatform.Linux,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(10),
        Direction = TestDirection.Down,
        Streams = 1,
        DurationS = 10,
        MeanBps = 100_000_000,
        PeakBps = 120_000_000,
        PerStreamBps = [100_000_000],
        PingMinMs = 1,
        PingAvgMs = 2,
        PingMaxMs = 3,
        PingP95Ms = 4,
        JitterMs = 0.5,
        LossPct = 0,
        PingSamples = 10,
    };
}
