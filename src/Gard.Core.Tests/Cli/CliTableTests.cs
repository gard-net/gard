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
        var plain = lines.Select(CliText.StripAnsi).ToArray();

        // La segunda columna arranca en la misma posición visible en todas las filas.
        var start = plain[0].IndexOf("STATUS", StringComparison.Ordinal);
        Assert.Equal(start, plain[2].IndexOf("ready", StringComparison.Ordinal));
        Assert.Equal(start, plain[3].IndexOf("exce", StringComparison.Ordinal));
        Assert.All(lines.Where(l => l.Contains(CliTheme.Red)), l => Assert.Contains(CliTheme.Reset, l));
    }

    [Fact]
    public void Render_ShrinksWidestColumnToFitTerminal()
    {
        var narrow = new CliTerminal(CliStyleOptions.Default, forceInteractive: true, forceWidth: 40);
        var columns = new[] { new CliTableColumn("NAME", 60), new CliTableColumn("OS", 8) };
        var rows = new List<IReadOnlyList<string>> { new[] { new string('x', 60), "linux" } };

        var lines = CliTable.Render(narrow, columns, rows).Split('\n').Select(l => CliText.StripAnsi(l.TrimEnd('\r')));

        Assert.All(lines, l => Assert.True(l.Length <= 40, $"{l.Length}: {l}"));
    }

    [Fact]
    public void Sparkline_ScalesToMaxAndResamples()
    {
        var spark = CliTable.Sparkline([0, 50, 100, 100], 4, CliSymbols.Unicode);
        Assert.Equal("▁▅██", spark);
        Assert.Equal(10, CliTable.Sparkline(Enumerable.Range(0, 100).Select(i => (double)i).ToArray(), 10, CliSymbols.Unicode).Length);
        Assert.Equal("", CliTable.Sparkline([], 10, CliSymbols.Unicode));
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
