using System.Text.Json;
using Gard.Cli;

namespace Gard.Core.Tests.Cli;

/// <summary>La app completa con stdout/stderr capturados: exit codes y separación de streams.</summary>
public class GardAppTests
{
    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var term = new CliTerminal(CliStyleOptions.PlainText, forceInteractive: false);
        using var ctx = new CliContext(term, stdout, stderr, hookConsole: false);
        var code = await GardApp.RunAsync(args, ctx);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Version_PrintsToStdout()
    {
        var (code, stdout, _) = await Run("version");
        Assert.Equal(0, code);
        Assert.StartsWith($"gard {GardCliInfo.VersionNumber}", stdout);
        Assert.Equal(0, (await Run("--version")).Code);
    }

    [Fact]
    public async Task UnknownCommand_IsUsageErrorWithSuggestion()
    {
        var (code, stdout, stderr) = await Run("tset");
        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("did you mean 'test'", stderr);
    }

    [Fact]
    public async Task UnknownOption_IsUsageErrorWithSuggestion()
    {
        var (code, _, stderr) = await Run("test", "127.0.0.1", "--durtion", "5");
        Assert.Equal(2, code);
        Assert.Contains("did you mean '--duration'", stderr);
        Assert.Contains("gard test --help", stderr);
    }

    [Fact]
    public async Task CommandHelp_ExitsZero()
    {
        var (code, stdout, _) = await Run("test", "-h");
        Assert.Equal(0, code);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("--duration", stdout);
    }

    [Fact]
    public async Task ConnectionRefused_IsRuntimeErrorWithActionableMessage()
    {
        // Puerto cerrado en loopback ⇒ refused inmediato.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var (code, stdout, stderr) = await Run("test", "127.0.0.1", "-p", port.ToString(), "--json");
        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("connection refused", stderr);
        Assert.DoesNotContain("--help", stderr);
    }

    [Fact]
    public async Task Doctor_Json_IsValidAndVersioned()
    {
        var (code, stdout, _) = await Run("doctor", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("gard.doctor/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.True(doc.RootElement.TryGetProperty("terminal", out _));
    }

    [Fact]
    public async Task Completions_PrintScript()
    {
        var (code, stdout, _) = await Run("completions", "bash");
        Assert.Equal(0, code);
        Assert.Contains("complete -F _gard gard", stdout);
        Assert.Equal(2, (await Run("completions")).Code);
    }

    [Fact]
    public async Task CsvHeader_StillWorks()
    {
        var (code, stdout, _) = await Run("test", "--csv-header");
        Assert.Equal(0, code);
        Assert.StartsWith("label,direction,streams", stdout);
    }

    [Fact]
    public async Task NoArgs_PrintsHelpWithUsageExit()
    {
        var (code, stdout, _) = await Run();
        Assert.Equal(2, code);
        Assert.Contains("Commands:", stdout);
    }
}
