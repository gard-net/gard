using Gard.Core.Handshake;
using Gard.Core.Transport;

namespace Gard.Cli;

public static class InfoCommand
{
    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        var options = CliOptions.ParseInfo(args);
        var t = ctx.Term;
        if (!options.Json && !t.Live) ctx.Err.WriteLine($"{t.Symbols.Arrow} connecting to {options.Host}:{options.Port}");

        await using var ctl = await TcpFrameTransport.ConnectAsync(options.Host, options.Port, cancellationToken: ctx.Interrupted)
            .ConfigureAwait(false);
        var hs = await ClientHandshake.PerformAsync(
            ctl, ClientSupport.Identity(), ClientSupport.PairingCode(options.PairCode, ctx), cancellationToken: ctx.Interrupted)
            .ConfigureAwait(false);
        var report = InfoReport.From(options.Host, options.Port, hs);

        if (options.Json)
        {
            ctx.Out.WriteLine(CliJson.Serialize(report, indented: !Console.IsOutputRedirected));
            return GardApp.Ok;
        }

        ctx.Out.WriteLine($"{t.Bold("gard info")} {t.Dim(t.Symbols.Dot)} {t.Cyan($"{options.Host}:{options.Port}")}");
        Row("protocol", $"LSP/{report.LspVersion}");
        Row("platform", report.Platform.ToString().ToLowerInvariant());
        Row("transports", string.Join(", ", report.Transports));
        Row("capabilities", string.Join(", ", report.Capabilities) + t.Dim($"  (0x{report.Caps})"));
        Row("clock rtt", $"{report.ClockRttMs:F2} ms");
        Row("session", t.Dim(report.SessionId));
        return GardApp.Ok;

        void Row(string label, string value) => ctx.Out.WriteLine($"  {t.Dim(label.PadRight(13))} {value}");
    }
}
