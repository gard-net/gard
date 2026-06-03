using Gard.Cli;
using Gard.Core.Measurement;
using Gard.Core.Protocol;

namespace Gard.Core.Tests.Cli;

public class CliOptionsTests
{
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
}
