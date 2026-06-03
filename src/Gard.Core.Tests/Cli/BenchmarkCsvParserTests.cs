using System.Diagnostics;

namespace Gard.Core.Tests.Cli;

public class BenchmarkCsvParserTests
{
    [Fact]
    public async Task ParseGardCsvScript_HandlesEscapedLabel()
    {
        var script = FindRepoFile("scripts/bench/parse_gard_csv.py");
        var row = string.Join(",", [
            "\"run,\"\"quoted\"\"\"", "down", "1", "10.00", "1.00", "65536", "", "tcp", "",
            "123.456", "130.000", "", "", "", "",
            "1.000", "2.000", "3.000", "4.000", "0.500", "0.000", "10",
            "", "9.000", "", "", "", "", "", "", "", "",
        ]);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                ArgumentList = { script },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        await process.StandardInput.WriteLineAsync(row);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("123.456,0.500,0.000,2.000,4.000,9.000", stdout.Trim());
        Assert.Equal("", stderr.Trim());
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}");
    }
}
