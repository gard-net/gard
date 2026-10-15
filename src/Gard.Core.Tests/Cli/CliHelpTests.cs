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
    [InlineData("test", "gard test <host> [options]")]
    [InlineData("watch", "gard watch <host> [options]")]
    [InlineData("info", "gard info <host>")]
    [InlineData("doctor", "gard doctor")]
    [InlineData("completions", "gard completions <bash|zsh|fish|powershell>")]
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

    [Fact]
    public void CommandHelp_ListsEveryOptionOfTheSpec()
    {
        // Help y validación salen de la misma spec: no pueden desalinearse.
        foreach (var command in CliSpec.Commands)
        {
            var help = CliHelp.Command(command);
            foreach (var option in command.Options)
                Assert.Contains(option.Name, help);
        }
    }

    [Fact]
    public void Highlight_OnlyAddsColorWhenEnabled()
    {
        var plain = new CliTerminal(CliStyleOptions.Default with { NoColor = true }, forceInteractive: true);
        var color = new CliTerminal(CliStyleOptions.Default, forceInteractive: true);
        var help = CliHelp.General();

        Assert.Equal(help, CliHelp.Highlight(help, plain));
        Assert.Contains("\u001b[1mUsage:", CliHelp.Highlight(help, color));
    }

    [Fact]
    public void Version_ComesFromAssembly_WithoutSourceRevision()
    {
        // Única fuente: Directory.Build.props (o -p:Version= en el release).
        Assert.Matches(@"^\d+\.\d+\.\d+", GardCliInfo.VersionNumber);
        Assert.DoesNotContain("+", GardCliInfo.VersionNumber);
        Assert.Equal($"gard-{GardCliInfo.VersionNumber}", GardCliInfo.AppVersion);
        Assert.StartsWith($"gard {GardCliInfo.VersionNumber} (LSP/", GardCliInfo.VersionString);
    }
}
