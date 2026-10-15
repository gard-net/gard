using Gard.Cli;

namespace Gard.Core.Tests.Cli;

public class CliTableTests
{
    private static readonly CliTerminal ColorTerm = new(CliStyleOptions.Default, forceInteractive: true);

    [Fact]
    public void VisibleLength_IgnoresAnsiEscapes()
    {
        Assert.Equal(5, CliText.VisibleLength(ColorTerm.Green("ready")));
        Assert.Equal(0, CliText.VisibleLength(""));
    }

    [Fact]
    public void Fit_PadsColoredTextByVisibleWidth()
    {
        var cell = CliText.Fit(ColorTerm.Green("ok"), 6);

        Assert.Equal(6, CliText.VisibleLength(cell));
        Assert.StartsWith(CliTheme.Green, cell);
        Assert.EndsWith("    ", cell);
    }

    [Fact]
    public void Fit_TruncatesColoredTextWithoutLosingReset()
    {
        // Antes: "\e[32mread." — texto equivocado y sin reset, el verde se derramaba.
        var cell = CliText.Fit(ColorTerm.Green("excellent"), 5);

        Assert.Equal(5, CliText.VisibleLength(cell));
        Assert.Contains("exce", cell);
        Assert.Contains(CliTheme.Reset, cell);
        Assert.EndsWith("…", cell);
    }

    [Fact]
    public void Render_ColoredCells_KeepRowsAligned()
    {
        var columns = new[] { new CliTableColumn("NAME", 8), new CliTableColumn("STATUS", 6) };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "lab-mac", ColorTerm.Green("ready") },
            new[] { "hp-ia", ColorTerm.Red("excellent") },
        };

        var lines = CliTable.Render(ColorTerm, columns, rows).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var widths = lines.Select(CliText.VisibleLength).Distinct().ToArray();

        Assert.Single(widths);
        Assert.All(lines.Where(l => l.Contains(CliTheme.Red)), l => Assert.Contains(CliTheme.Reset, l));
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("info")]
    [InlineData("doctor")]
    public void HintFor_CoversEveryCommand(string command)
    {
        Assert.Equal($"Try 'gard {command} --help'.", CliErrors.HintFor(command));
    }

    [Fact]
    public void HintFor_UnknownCommand_PointsToGeneralHelp()
    {
        Assert.Equal("Try 'gard --help'.", CliErrors.HintFor(null));
    }

    [Fact]
    public void ForcedInteractiveTerminal_IgnoresNoColorEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Assert.True(new CliTerminal(CliStyleOptions.Default, forceInteractive: true).Color);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previous);
        }
    }
}
