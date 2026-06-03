using Gard.Cli;

namespace Gard.Core.Tests.Cli;

public class CliHelpTests
{
    [Fact]
    public void GeneralHelp_HasProfessionalSections()
    {
        var help = CliHelp.General();

        Assert.Contains("Usage:", help);
        Assert.Contains("Commands:", help);
        Assert.Contains("Examples:", help);
        Assert.Contains("Output formats:", help);
        Assert.Contains("gard help <command>", help);
    }

    [Theory]
    [InlineData("scan", "gard scan [--seconds N]")]
    [InlineData("host", "gard host [--name NAME]")]
    [InlineData("test", "gard test --host <ip-or-name>")]
    [InlineData("watch", "gard watch --host <ip-or-name>")]
    [InlineData("info", "gard info --host <ip-or-name>")]
    [InlineData("doctor", "gard doctor")]
    public void CommandHelp_UsesCommandSpecificUsage(string command, string expected)
    {
        var help = CliHelp.ForCommand(command);

        Assert.Contains("Usage:", help);
        Assert.Contains(expected, help);
    }

    [Fact]
    public void GeneralHelp_IncludesNewCommandsAndStyleFlags()
    {
        var help = CliHelp.General();

        Assert.Contains("watch", help);
        Assert.Contains("info", help);
        Assert.Contains("doctor", help);
        Assert.Contains("--plain", help);
        Assert.Contains("--style STYLE", help);
    }
}
