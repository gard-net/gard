using System.Globalization;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// Muestras cortas repetidas. Humano: encabezado una vez y una fila por muestra
/// (antes se re-imprimía la tabla entera cada vez). <c>--json</c>: NDJSON, un
/// <see cref="TestReport"/> por línea, apto para <c>jq</c> o para ingestar.
/// </summary>
public static class WatchCommand
{
    private static readonly CliTableColumn[] Columns =
    [
        new("TIME", 8),
        new("DOWN", 12, Right: true),
        new("UP", 12, Right: true),
        new("P95", 9, Right: true),
        new("JITTER", 9, Right: true),
        new("LOSS", 7, Right: true),
        new("QUALITY", 9),
    ];

    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        var options = CliOptions.ParseWatch(args);
        var term = ctx.Term;
        var p = options.Test.Parameters;

        if (!options.Json)
        {
            ctx.Err.WriteLine(term.Dim(
                $"watching {options.Host} every {options.IntervalS.ToString("0.#", CultureInfo.InvariantCulture)}s " +
                $"{term.Symbols.Dot} {p.DurationS.ToString("0.#", CultureInfo.InvariantCulture)}s samples " +
                $"{term.Symbols.Dot} Ctrl+C to stop"));
            ctx.Out.WriteLine(CliTable.RenderHeader(term, Columns));
        }

        var sampleTimeout = TimeSpan.FromSeconds(p.DurationS + p.WarmupS + 15);
        // Un solo proveedor: si hay que preguntar el código, se pregunta una vez.
        var pairingCode = ClientSupport.PairingCode(options.Test.PairCode, ctx);
        var taken = 0;
        var failures = 0;

        while (!ctx.Interrupted.IsCancellationRequested && (options.Count is null || taken < options.Count))
        {
            try
            {
                using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Interrupted);
                sampleCts.CancelAfter(sampleTimeout);
                TestResult result;
                await using (var progress = new LiveProgress(term, ctx.Err, p.DurationS, enabled: !options.Json && term.Live))
                {
                    result = await MeasurementRunner.RunAsync(options.Test, progress, pairingCode, sampleCts.Token).ConfigureAwait(false);
                }
                ctx.Out.WriteLine(options.Json
                    ? CliJson.Serialize(TestReport.From(result, p, options.Test.Label))
                    : CliTable.RenderRow(term, Columns, Row(term, result)));
                ctx.Out.Flush();
            }
            catch (Exception) when (ctx.Interrupted.IsCancellationRequested)
            {
                break;
            }
            catch (LandspeedException ex) when (ex.Kind is LandspeedErrorKind.PairingRequired or LandspeedErrorKind.PairingInvalid)
            {
                // Reintentar con el mismo código sólo quemaría el rate limit del host.
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or LandspeedException { Kind: LandspeedErrorKind.Cancelled })
            {
                failures++;
                ctx.Warn($"sample timed out after {sampleTimeout.TotalSeconds:F0}s");
            }
            catch (Exception ex)
            {
                failures++;
                ctx.Warn($"sample failed: {CliErrors.Describe(ex)}");
            }
            taken++;

            if (options.Count is { } count && taken >= count) break;
            try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalS), ctx.Interrupted).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        if (ctx.Interrupted.IsCancellationRequested) return GardApp.Interrupted;
        // Con --count, que todas las muestras fallen es un error para scripts.
        return failures > 0 && failures == taken ? CliErrors.RuntimeExitCode : GardApp.Ok;
    }

    private static IReadOnlyList<string> Row(CliTerminal term, TestResult r)
    {
        var down = r.ThroughputDown?.MeanBps ?? (r.Direction == TestDirection.Down ? r.MeanBps : 0);
        var up = r.ThroughputUp?.MeanBps ?? (r.Direction == TestDirection.Up ? r.MeanBps : 0);
        var quality = CliQuality.Grade(r);
        return
        [
            DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            down == 0 ? term.Dim("-") : CliHumanFormatter.FormatBps(down),
            up == 0 ? term.Dim("-") : CliHumanFormatter.FormatBps(up),
            $"{r.PingP95Ms.ToString("F1", CultureInfo.InvariantCulture)} ms",
            $"{r.JitterMs.ToString("F1", CultureInfo.InvariantCulture)} ms",
            $"{r.LossPct.ToString("F2", CultureInfo.InvariantCulture)}%",
            CliQuality.Paint(term, quality),
        ];
    }
}
