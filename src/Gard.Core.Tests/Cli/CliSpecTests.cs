using System.Text.Json;
using Gard.Cli;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class CliSpecTests
{
    private static string[] Normalize(string command, params string[] args) =>
        CliSpec.Normalize(CliSpec.Find(command)!, args).Args;

    [Fact]
    public void ShortFlags_MapToLongForms_IperfStyle()
    {
        Assert.Equal(
            ["10.0.0.7", "--duration", "10", "--streams", "4", "--payload", "1200", "--bitrate", "200M", "--port", "9000"],
            Normalize("test", "10.0.0.7", "-t", "10", "-P", "4", "-l", "1200", "-b", "200M", "-p", "9000"));
    }

    [Fact]
    public void Shortcuts_ExpandToTheirOptions()
    {
        Assert.Equal(["--transport", "udp", "--format", "json"], Normalize("test", "-u", "-J"));
        Assert.Equal(["--format", "csv"], Normalize("test", "--csv"));
    }

    [Fact]
    public void EqualsSyntax_IsSplit()
    {
        Assert.Equal(["--duration", "5", "--label", "a=b"], Normalize("test", "--duration=5", "--label=a=b"));
    }

    [Fact]
    public void NegativeLookingValues_AreValues()
    {
        // "-1M" es el valor de --bitrate (inválido, pero lo decide el parser, no la spec).
        Assert.Equal(["--bitrate", "-1M"], Normalize("test", "--bitrate", "-1M"));
    }

    [Fact]
    public void UnknownOption_FailsWithSuggestion()
    {
        var ex = Assert.Throws<CliOptionException>(() => Normalize("test", "10.0.0.7", "--durtion", "5"));
        Assert.Contains("unknown option '--durtion'", ex.Message);
        Assert.Contains("did you mean '--duration'", ex.Message);
    }

    [Fact]
    public void UnknownOption_WithoutCloseMatch_HasNoSuggestion()
    {
        var ex = Assert.Throws<CliOptionException>(() => Normalize("scan", "--frobnicate"));
        Assert.DoesNotContain("did you mean", ex.Message);
    }

    [Fact]
    public void DeprecatedAlias_IsAcceptedWithWarning()
    {
        var (args, warnings) = CliSpec.Normalize(CliSpec.Find("test")!, ["--dir", "up"]);
        Assert.Equal(["--direction", "up"], args);
        Assert.Single(warnings);
        Assert.Contains("--direction", warnings[0]);
    }

    [Fact]
    public void ExtraPositional_IsRejected()
    {
        Assert.Throws<CliOptionException>(() => Normalize("test", "10.0.0.7", "10.0.0.8"));
        Assert.Throws<CliOptionException>(() => Normalize("doctor", "now"));
    }

    [Fact]
    public void MissingValue_IsRejected()
    {
        var ex = Assert.Throws<CliOptionException>(() => Normalize("test", "10.0.0.7", "--duration"));
        Assert.Contains("--duration requires a value", ex.Message);
        Assert.Throws<CliOptionException>(() => Normalize("test", "--json=yes"));
    }

    [Fact]
    public void Help_IsRecognisedInAnyForm()
    {
        Assert.Equal(["--help"], Normalize("scan", "-h"));
        Assert.Equal(["--help"], Normalize("host", "--help"));
    }

    [Fact]
    public void EveryCommandOption_IsUniqueWithinItsCommand()
    {
        foreach (var c in CliSpec.Commands)
        {
            var names = c.Options.SelectMany(o => o.Short is null ? [o.Name] : new[] { o.Name, o.Short }).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }
}

public class CompletionsTests
{
    [Theory]
    [InlineData("bash", "complete -F _gard gard")]
    [InlineData("zsh", "#compdef gard")]
    [InlineData("fish", "complete -c gard")]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    public void Script_CoversCommandsAndOptions(string shell, string marker)
    {
        var script = CompletionsCommand.Generate(shell);

        Assert.Contains(marker, script);
        foreach (var c in CliSpec.Commands) Assert.Contains(c.Name, script);
        Assert.Contains("duration", script);
        Assert.Contains("bidir", script); // valores de --direction
    }

    [Fact]
    public void UnknownShell_IsUsageError()
    {
        Assert.Throws<CliOptionException>(() => CompletionsCommand.Generate("tcsh"));
    }
}

public class CliJsonTests
{
    private static TestParameters Params() => new()
    {
        Direction = TestDirection.Down, DurationS = 2, Streams = 2, PayloadSize = 65_536, WarmupS = 1,
    };

    private static TestResult Result() => new()
    {
        SessionId = "s-1",
        PeerName = "10.0.0.7",
        PeerPlatform = PeerPlatform.Linux,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(2),
        Direction = TestDirection.Down,
        Streams = 2,
        DurationS = 2,
        MeanBps = 900_000_000,
        PeakBps = 950_000_000,
        PerStreamBps = [450_000_000, 450_000_000],
        PingMinMs = 0.2, PingAvgMs = 0.4, PingMaxMs = 1.1, PingP95Ms = 0.9,
        JitterMs = 0.1, LossPct = 0, PingSamples = 30,
    };

    [Fact]
    public void TestReport_IsSnakeCaseVersionedAndLowercaseEnums()
    {
        var json = CliJson.Serialize(TestReport.From(Result(), Params(), "lab"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("gard.test/1", root.GetProperty("schema").GetString());
        Assert.Equal(GardCliInfo.VersionNumber, root.GetProperty("gard_version").GetString());
        Assert.Equal("1.2", root.GetProperty("lsp_version").GetString());
        Assert.Equal("lab", root.GetProperty("label").GetString());
        Assert.Equal(900_000_000UL, root.GetProperty("throughput").GetProperty("mean_bps").GetUInt64());
        Assert.Equal("down", root.GetProperty("test").GetProperty("direction").GetString());
        Assert.Equal("tcp", root.GetProperty("test").GetProperty("transport").GetString());
        Assert.Equal("linux", root.GetProperty("peer").GetProperty("platform").GetString());
        Assert.Equal(0.9, root.GetProperty("latency").GetProperty("p95_ms").GetDouble());
        Assert.Equal("excellent", root.GetProperty("quality").GetString());
        Assert.False(root.TryGetProperty("udp", out _)); // nulls omitidos
        Assert.DoesNotContain("MeanBps", json);
    }

    [Fact]
    public void Indented_IsValidAndEquivalent()
    {
        var report = TestReport.From(Result(), Params(), null);
        var compact = CliJson.Serialize(report);
        var pretty = CliJson.Serialize(report, indented: true);

        Assert.DoesNotContain("\n", compact);
        Assert.Contains("\n", pretty);
        Assert.Equal(
            JsonDocument.Parse(compact).RootElement.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""),
            JsonDocument.Parse(pretty).RootElement.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""));
    }

    [Fact]
    public void CapabilityNames_FollowSpec()
    {
        Assert.Equal(["parallel_streams", "udp_data_plane"],
            CliJson.CapabilityNames(Capabilities.ParallelStreams | Capabilities.UdpDataPlane));
    }
}
