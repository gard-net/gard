using System.Globalization;
using Gard.Core.Discovery;
using Gard.Core.Protocol;

namespace Gard.Cli;

public static class ScanCommand
{
    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        var options = CliOptions.ParseScan(args);
        var term = ctx.Term;
        await using var browser = new MdnsPeerBrowser();
        await browser.StartAsync().ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Interrupted);
        cts.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        var refresh = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    browser.Refresh();
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        });
        var spinner = term.Live && !options.Json
            ? Task.Run(() => SpinAsync(ctx, browser, options.Seconds, cts.Token))
            : Task.CompletedTask;
        if (!term.Live && !options.Json)
            ctx.Err.WriteLine($"{term.Symbols.Arrow} scanning _landspeed._tcp for {options.Seconds}s");

        try { await foreach (var _ in browser.Events.ReadAllAsync(cts.Token).ConfigureAwait(false)) { } }
        catch (OperationCanceledException) { }
        await Task.WhenAll(refresh, spinner).ConfigureAwait(false);
        if (ctx.Interrupted.IsCancellationRequested) return GardApp.Interrupted;

        var peers = browser.CurrentPeers.OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
        if (options.Json)
        {
            ctx.Out.WriteLine(CliJson.Serialize(new ScanReport
            {
                DurationS = options.Seconds,
                Peers = peers.Select(ScanPeerReport.From).ToArray(),
            }, indented: !Console.IsOutputRedirected));
            return GardApp.Ok;
        }

        if (peers.Count == 0)
        {
            ctx.Out.WriteLine($"{term.Yellow(term.Symbols.Warn)} no peers found in {options.Seconds}s");
            ctx.Err.WriteLine(term.Dim("  is 'gard host' running on the same LAN/VLAN, and is mDNS (UDP 5353) allowed?"));
            return GardApp.Ok;
        }

        var rows = peers.Select(p => (IReadOnlyList<string>)
        [
            term.Bold(p.DeviceName),
            p.Host is null ? term.Dim("resolving") : $"{FormatHost(p.Host)}:{p.Port}",
            p.Platform.ToString().ToLowerInvariant(),
            $"{p.ProtocolVersionMajor}.x",
            p.Caps.HasFlag(Capabilities.UdpDataPlane) ? "tcp, udp" : "tcp",
        ]).ToList();
        ctx.Out.WriteLine(CliTable.Render(term,
            [new("NAME", 28), new("ADDRESS", 46), new("OS", 8), new("LSP", 4), new("TRANSPORTS", 10)], rows));
        ctx.Out.WriteLine(term.Dim($"{peers.Count} peer{(peers.Count == 1 ? "" : "s")} found {term.Symbols.Dot} test one with: gard test <address>"));
        return GardApp.Ok;
    }

    // IPv6 entre corchetes para que host:puerto no sea ambiguo.
    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static async Task SpinAsync(CliContext ctx, MdnsPeerBrowser browser, int seconds, CancellationToken ct)
    {
        var s = ctx.Term.Symbols;
        var started = DateTime.UtcNow;
        var frame = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                var count = browser.CurrentPeers.Count;
                LiveProgress.Redraw(ctx.Term, ctx.Err,
                    $"{ctx.Term.Cyan(s.Spinner[frame++ % s.Spinner.Length])} scanning  " +
                    ctx.Term.Dim($"{elapsed.ToString("F0", CultureInfo.InvariantCulture)}/{seconds}s  ") +
                    $"{count} peer{(count == 1 ? "" : "s")}");
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            LiveProgress.Clear(ctx.Term, ctx.Err);
        }
    }
}
