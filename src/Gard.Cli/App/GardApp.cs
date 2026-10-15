using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// Punto de entrada: opciones globales, validación contra <see cref="CliSpec"/>,
/// despacho al comando y traducción de excepciones a exit codes.
/// </summary>
public static class GardApp
{
    public const int Ok = 0;
    public const int Interrupted = 130;

    public static async Task<int> RunAsync(string[] args)
    {
        CliTerminal term;
        string[] cleanArgs;
        try
        {
            term = new CliTerminal(CliOptions.ParseGlobalStyle(args));
            cleanArgs = CliOptions.StripGlobalOptions(args);
        }
        catch (CliOptionException ex)
        {
            Console.Error.WriteLine($"gard: {ex.Message}");
            Console.Error.WriteLine(CliErrors.HintFor(null));
            return CliErrors.UsageExitCode;
        }

        using var ctx = new CliContext(term);
        return await RunAsync(cleanArgs, ctx).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        if (args.Length == 0)
        {
            ctx.Out.WriteLine(CliHelp.Highlight(CliHelp.General(), ctx.Term));
            return CliErrors.UsageExitCode;
        }

        var name = args[0];
        var rest = args[1..];
        switch (name)
        {
            case "-h" or "--help":
                ctx.Out.WriteLine(CliHelp.Highlight(CliHelp.General(), ctx.Term));
                return Ok;
            case "-v" or "--version":
                ctx.Out.WriteLine(GardCliInfo.VersionString);
                return Ok;
            case "help":
                ctx.Out.WriteLine(CliHelp.Highlight(rest.Length > 0 ? CliHelp.ForCommand(rest[0]) : CliHelp.General(), ctx.Term));
                return Ok;
        }

        if (CliSpec.Find(name) is not { } command)
        {
            var hint = CliSpec.Suggest(name, CliSpec.Commands.Select(c => c.Name));
            ctx.Err.WriteLine($"gard: unknown command '{name}'" + (hint is null ? "" : $"; did you mean '{hint}'?"));
            ctx.Err.WriteLine(CliErrors.HintFor(null));
            return CliErrors.UsageExitCode;
        }

        try
        {
            var (normalized, warnings) = CliSpec.Normalize(command, rest);
            foreach (var w in warnings) ctx.Warn(w);
            if (normalized.Contains("--help"))
            {
                ctx.Out.WriteLine(CliHelp.Highlight(CliHelp.Command(command), ctx.Term));
                return Ok;
            }

            return command.Name switch
            {
                "scan" => await ScanCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "host" => await HostCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "test" => await TestCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "watch" => await WatchCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "tui" => await TuiCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "info" => await InfoCommand.RunAsync(normalized, ctx).ConfigureAwait(false),
                "doctor" => DoctorCommand.Run(normalized, ctx),
                "completions" => CompletionsCommand.Run(normalized, ctx),
                "version" => PrintVersion(ctx),
                _ => throw new InvalidOperationException($"comando sin implementar: {command.Name}"),
            };
        }
        catch (CliOptionException ex)
        {
            ctx.Err.WriteLine($"gard: {ex.Message}");
            ctx.Err.WriteLine(CliErrors.HintFor(command.Name));
            return CliErrors.UsageExitCode;
        }
        catch (Exception ex) when (ctx.Interrupted.IsCancellationRequested && IsCancellation(ex))
        {
            ctx.Err.WriteLine();
            ctx.Err.WriteLine(ctx.Term.Dim("interrupted"));
            return Interrupted;
        }
        catch (LandspeedException ex) when (ex.Kind is LandspeedErrorKind.PairingRequired or LandspeedErrorKind.PairingInvalid)
        {
            ctx.Err.WriteLine($"gard: {CliErrors.PairingMessage(ex)}");
            return CliErrors.RuntimeExitCode;
        }
        catch (Exception ex)
        {
            // Error de ejecución (conexión, timeout, protocolo): el uso era
            // correcto, así que no se sugiere --help.
            ctx.Err.WriteLine($"gard: {CliErrors.Describe(ex)}");
            return CliErrors.RuntimeExitCode;
        }
    }

    private static int PrintVersion(CliContext ctx)
    {
        ctx.Out.WriteLine(GardCliInfo.VersionString);
        return Ok;
    }

    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException or LandspeedException { Kind: LandspeedErrorKind.Cancelled };
}
