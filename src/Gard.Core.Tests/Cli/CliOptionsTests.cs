using Gard.Cli;
using Gard.Core.Measurement;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class CliOptionsTests
{
    [Fact]
    public void ParseGlobalStyle_StripsGlobalFlags()
    {
        var style = CliOptions.ParseGlobalStyle(["--style", "plain", "--no-color", "test", "--host", "127.0.0.1"]);
        var args = CliOptions.StripGlobalOptions(["--style", "plain", "--no-color", "test", "--host", "127.0.0.1"]);

        Assert.True(style.NoColor);
        Assert.Equal("plain", style.Style);
        Assert.Equal(["test", "--host", "127.0.0.1"], args);
    }

    [Fact]
    public void ParseTest_DefaultsToTcpDown()
    {
        var options = CliOptions.ParseTest(["--host", "192.168.1.50"]);

        Assert.Equal("192.168.1.50", options.Host);
        Assert.Equal(7737, options.Port);
        Assert.Equal("human", options.Format);
        Assert.Equal(TestDirection.Down, options.Parameters.Direction);
        Assert.Equal(TestTransport.Tcp, options.Parameters.Transport);
        Assert.Equal(1, options.Parameters.Streams);
        Assert.Equal(10, options.Parameters.DurationS);
    }

    [Fact]
    public void ParseTest_AcceptsPositionalHostAndUdp()
    {
        var options = CliOptions.ParseTest([
            "10.0.0.7",
            "--transport", "udp",
            "--direction", "up",
            "--streams", "4",
            "--payload", "1200",
            "--bitrate", "100M",
            "--format", "csv",
            "--label", "run-a",
        ]);

        Assert.Equal("10.0.0.7", options.Host);
        Assert.Equal(TestTransport.Udp, options.Parameters.Transport);
        Assert.Equal(TestDirection.Up, options.Parameters.Direction);
        Assert.Equal(4, options.Parameters.Streams);
        Assert.Equal(1200, options.Parameters.PayloadSize);
        Assert.Equal(100_000_000UL, options.Parameters.TargetBitrateBps);
        Assert.Equal("csv", options.Format);
        Assert.Equal("run-a", options.Label);
    }

    [Theory]
    [InlineData("--streams", "abc")]
    [InlineData("--port", "70000")]
    [InlineData("--duration", "0")]
    [InlineData("--warmup", "-1")]
    [InlineData("--gap", "NaN")]
    [InlineData("--payload", "10")]
    [InlineData("--bitrate", "-1M")]
    [InlineData("--streams", "33")]
    [InlineData("--duration", "3601")]
    [InlineData("--warmup", "61")]
    [InlineData("--gap", "61")]
    public void ParseTest_RejectsInvalidValues(string name, string value)
    {
        Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest(["--host", "127.0.0.1", name, value]));
    }

    [Fact]
    public void ParseTest_RejectsMissingOptionValue()
    {
        var ex = Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest(["--host", "127.0.0.1", "--port"]));
        Assert.Contains("--port requires a value", ex.Message);
    }

    [Fact]
    public void ParseTest_RejectsUdpSequentialBidir()
    {
        var ex = Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest([
                "--host", "127.0.0.1",
                "--transport", "udp",
                "--payload", "1200",
                "--direction", "bidir",
                "--bidir-mode", "sequential",
            ]));

        Assert.Contains("UDP does not support", ex.Message);
    }

    [Fact]
    public void ParseTest_UdpBidir_NeedsTwoStreams()
    {
        var ex = Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest(["10.0.0.7", "--transport", "udp", "--payload", "1200", "--direction", "bidir"]));
        Assert.Contains("--streams 2", ex.Message);

        var ok = CliOptions.ParseTest(["10.0.0.7", "--transport", "udp", "--payload", "1200", "--direction", "bidir", "--streams", "2"]);
        Assert.Equal(2, ok.Parameters.Streams);
    }

    [Fact]
    public void ParseTest_RejectsOversizedUdpPayload()
    {
        var ex = Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest([
                "--host", "127.0.0.1",
                "--transport", "udp",
                "--payload", (UdpDataPlane.MaxPayloadSize + 1).ToString(),
            ]));

        Assert.Contains("--payload must be between", ex.Message);
    }

    [Fact]
    public void ParseTest_RejectsTcpPayloadAboveHostLimit()
    {
        // El host rechaza > MaxTcpPayloadSize con 3004; el CLI falla antes y claro.
        var ex = Assert.Throws<CliOptionException>(() =>
            CliOptions.ParseTest([
                "--host", "127.0.0.1",
                "--payload", (TestLimits.MaxTcpPayloadSize + 1).ToString(),
            ]));

        Assert.Contains("--payload must be between", ex.Message);
    }

    [Fact]
    public void ParseTest_AcceptsTcpPayloadAtHostLimit()
    {
        var options = CliOptions.ParseTest([
            "--host", "127.0.0.1",
            "--payload", TestLimits.MaxTcpPayloadSize.ToString(),
        ]);

        Assert.Equal(TestLimits.MaxTcpPayloadSize, options.Parameters.PayloadSize);
    }

    [Fact]
    public void ParseHost_PairingIsOffByDefault()
    {
        var options = CliOptions.ParseHost([], "lab");
        Assert.False(options.RequirePairing);
        Assert.Null(options.PairCode);
    }

    [Fact]
    public void ParseHost_PairFlag_RequiresGeneratedCode()
    {
        var options = CliOptions.ParseHost(["--pair"], "lab");
        Assert.True(options.RequirePairing);
        Assert.Null(options.PairCode);
    }

    [Fact]
    public void ParseHost_PairCode_ImpliesPairing()
    {
        var options = CliOptions.ParseHost(["--pair-code", "012345"], "lab");
        Assert.True(options.RequirePairing);
        Assert.Equal("012345", options.PairCode);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("1234567")]
    public void PairCode_MustBeSixDigits(string code)
    {
        Assert.Throws<CliOptionException>(() => CliOptions.ParseHost(["--pair-code", code], "lab"));
        Assert.Throws<CliOptionException>(() => CliOptions.ParseTest(["10.0.0.7", "--pair-code", code]));
        Assert.Throws<CliOptionException>(() => CliOptions.ParseInfo(["10.0.0.7", "--pair-code", code]));
    }

    [Fact]
    public void ParseTestInfoWatch_CarryPairCode()
    {
        Assert.Equal("654321", CliOptions.ParseTest(["10.0.0.7", "--pair-code", "654321"]).PairCode);
        Assert.Equal("654321", CliOptions.ParseInfo(["10.0.0.7", "--pair-code", "654321"]).PairCode);
        Assert.Equal("654321", CliOptions.ParseWatch(["10.0.0.7", "--pair-code", "654321"]).Test.PairCode);
        Assert.Null(CliOptions.ParseTest(["10.0.0.7"]).PairCode);
    }

    [Fact]
    public void PairingMessage_IsActionable()
    {
        Assert.Contains("--pair-code", CliErrors.PairingMessage(Gard.Core.Protocol.LandspeedException.PairingRequired()));
        Assert.Contains("demasiados", CliErrors.PairingMessage(
            Gard.Core.Protocol.LandspeedException.PairingInvalid("demasiados intentos fallidos")));
    }

    [Fact]
    public void ParseWatch_UsesShortMeasurementDefaults()
    {
        var options = CliOptions.ParseWatch(["192.168.1.50", "--interval", "2"]);

        Assert.Equal("192.168.1.50", options.Host);
        Assert.Equal(2, options.IntervalS);
        Assert.Equal(3, options.Test.Parameters.DurationS);
        Assert.Equal(0, options.Test.Parameters.WarmupS);
        Assert.Equal(TestDirection.Down, options.Test.Parameters.Direction);
    }

    [Fact]
    public void ParseInfo_AcceptsPositionalHost()
    {
        var options = CliOptions.ParseInfo(["192.168.1.50"]);

        Assert.Equal("192.168.1.50", options.Host);
        Assert.Equal(7737, options.Port);
    }
}
