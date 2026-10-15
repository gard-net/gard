using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
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

        var discovered = browser.CurrentPeers.OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
        // mDNS cachea anuncios (PTR hasta 75 min): un host cerrado sin "goodbye"
        // seguía apareciendo. Un connect TCP corto separa vivos de fantasmas.
        var probes = await Task.WhenAll(discovered.Select(p => ProbeAsync(p, ctx.Interrupted))).ConfigureAwait(false);
        var results = discovered.Zip(probes, (p, ms) => (Peer: p, ConnectMs: ms)).ToList();

        if (options.Json)
        {
            ctx.Out.WriteLine(CliJson.Serialize(new ScanReport
            {
                DurationS = options.Seconds,
                Peers = results.Select(r => ScanPeerReport.From(r.Peer, r.ConnectMs)).ToArray(),
            }, indented: !Console.IsOutputRedirected));
            return GardApp.Ok;
        }

        var shown = options.All ? results : results.Where(r => r.ConnectMs is not null).ToList();
        var stale = results.Count - results.Count(r => r.ConnectMs is not null);
        if (shown.Count == 0)
        {
            ctx.Out.WriteLine($"{term.Yellow(term.Symbols.Warn)} no reachable peers found in {options.Seconds}s");
            ctx.Err.WriteLine(term.Dim(stale > 0
                ? $"  {stale} cached announcement{(stale == 1 ? "" : "s")} did not answer; run with --all to list them"
                : "  is 'gard host' running on the same LAN/VLAN, and is mDNS (UDP 5353) allowed?"));
            return GardApp.Ok;
        }

        var rows = shown.Select(r => (IReadOnlyList<string>)
        [
            term.Bold(r.Peer.DeviceName),
            r.Peer.Host is null ? term.Dim("resolving") : $"{FormatHost(r.Peer.Host)}:{r.Peer.Port}",
            r.Peer.Platform.ToString().ToLowerInvariant(),
            $"{r.Peer.ProtocolVersionMajor}.x",
            r.Peer.Caps.HasFlag(Capabilities.UdpDataPlane) ? "tcp, udp" : "tcp",
            r.ConnectMs is { } ms ? $"{ms.ToString("F1", CultureInfo.InvariantCulture)} ms" : term.Red("no answer"),
        ]).ToList();
        ctx.Out.WriteLine(CliTable.Render(term,
            [new("NAME", 28), new("ADDRESS", 46), new("OS", 8), new("LSP", 4), new("TRANSPORTS", 10), new("CONNECT", 10, Right: true)], rows));
        var footer = $"{shown.Count} peer{(shown.Count == 1 ? "" : "s")} {term.Symbols.Dot} test one with: gard test <address>";
        if (!options.All && stale > 0) footer += $" {term.Symbols.Dot} {stale} stale hidden (--all)";
        ctx.Out.WriteLine(term.Dim(footer));
        return GardApp.Ok;
    }

    /// <summary>Tiempo de connect TCP en ms, o <c>null</c> si no responde en 800 ms.</summary>
    private static async Task<double?> ProbeAsync(DiscoveredPeer peer, CancellationToken ct)
    {
        if (peer.Host is null || peer.Port is not { } port) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(800);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(peer.Host, port, cts.Token).ConfigureAwait(false);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
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
