namespace Gard.Cli;

public static class TestCommand
{
    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        if (CliOptions.HasFlag(args, "--csv-header"))
        {
            ctx.Out.WriteLine(CliCsvFormatter.Header());
            return GardApp.Ok;
        }

        var options = CliOptions.ParseTest(args);
        var human = options.Format == "human";
        // Progreso en vivo sólo sobre un stderr interactivo (también con --json:
        // va a stderr y se borra). Sin TTY, las líneas de fase sólo en modo humano
        // para no mezclar ruido en logs de automatización.
        var showProgress = !options.Quiet && (ctx.Term.Live || human);
        TestResultEnvelope envelope;
        await using (var progress = new LiveProgress(ctx.Term, ctx.Err, options.Parameters.DurationS, showProgress))
        {
            var result = await MeasurementRunner.RunAsync(
                options, progress, ClientSupport.PairingCode(options.PairCode, ctx), ctx.Interrupted).ConfigureAwait(false);
            envelope = new TestResultEnvelope(result, progress.History);
        }

        switch (options.Format)
        {
            case "json":
                ctx.Out.WriteLine(CliJson.Serialize(
                    TestReport.From(envelope.Result, options.Parameters, options.Label), indented: !Console.IsOutputRedirected));
                break;
            case "csv":
                ctx.Out.WriteLine(CliCsvFormatter.Format(envelope.Result, options.Parameters, options.Label));
                break;
            default:
                ctx.Out.WriteLine(CliHumanFormatter.Format(envelope.Result, ctx.Term, envelope.History));
                break;
        }
        return GardApp.Ok;
    }

    private sealed record TestResultEnvelope(Gard.Core.Models.TestResult Result, IReadOnlyList<double> History);
}
