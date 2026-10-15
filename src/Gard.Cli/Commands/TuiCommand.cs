using System.Collections.Concurrent;
using Gard.Core.Discovery;
using Gard.Core.Measurement;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// <c>gard tui</c>: pantalla completa con peers descubiertos, mediciones en
/// vivo e historial. Este archivo es el orquestador (I/O, red, tiempos); el
/// estado vive en <see cref="TuiState"/> y el dibujo en <see cref="TuiView"/>.
/// </summary>
public static class TuiCommand
{
    private static readonly TimeSpan DiscoveryEvery = TimeSpan.FromSeconds(2);
    // Cada sonda aparece en el log del host: cada 30 s y no durante un test.
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new CliOptionException("gard tui needs an interactive terminal; use 'gard watch' for logs and scripts");
        if (!WindowsConsole.EnableVirtualTerminal())
            throw new CliOptionException("this console cannot draw full-screen apps; use Windows Terminal, or 'gard watch'");

        var term = ctx.Term;
        var state = new TuiState();
        var pairCodes = new ConcurrentDictionary<string, string>();

        var options = CliOptions.ParseTui(args);
        if (options.Host is { } initial)
        {
            var peer = state.Upsert(new TuiPeer { Name = initial, Host = initial, Port = options.Port, Manual = true });
            state.Select(peer.Key);
            if (options.PairCode is { } code) pairCodes[peer.Key] = code;
        }

        await using var browser = new MdnsPeerBrowser();
        try { await browser.StartAsync().ConfigureAwait(false); }
        catch (Exception ex) { state.SetStatus($"mDNS unavailable ({ex.Message}); press 'a' to add hosts", error: true); }

        using var screen = new TuiConsole(ctx.Out);
        CancellationTokenSource? runCts = null;
        Task runTask = Task.CompletedTask;
        Task probeTask = Task.CompletedTask;
        var lastDiscovery = DateTime.MinValue;
        var lastProbe = DateTime.MinValue;

        screen.Enter();
        try
        {
            var quit = false;
            while (!quit && !ctx.Interrupted.IsCancellationRequested)
            {
                while (screen.Keys.TryRead(out var key))
                {
                    TuiAction action;
                    lock (state.Gate) action = state.HandleKey(key);
                    switch (action)
                    {
                        case TuiAction.Quit:
                            quit = true;
                            break;
                        case TuiAction.RunTest when runTask.IsCompleted:
                            (runCts, runTask) = StartRun(state, pairCodes, ctx);
                            break;
                        case TuiAction.CancelTest:
                            runCts?.Cancel();
                            break;
                        case TuiAction.Rescan:
                            try { browser.Refresh(); } catch { }
                            lastProbe = DateTime.MinValue;
                            lock (state.Gate) state.SetStatus("rescanning");
                            break;
                    }
                }
                if (quit) break;

                var now = DateTime.UtcNow;
                if (now - lastDiscovery >= DiscoveryEvery)
                {
                    lastDiscovery = now;
                    MergeDiscovered(state, browser);
                }

                bool idle, unprobed;
                lock (state.Gate)
                {
                    idle = state.Running is null;
                    unprobed = state.Peers.Any(p => !p.Probed);
                }
                if (idle && probeTask.IsCompleted && (unprobed || now - lastProbe >= ProbeEvery))
                {
                    lastProbe = now;
                    probeTask = ProbeAllAsync(state, ctx.Interrupted);
                }

                bool watchDue;
                lock (state.Gate)
                {
                    watchDue = state.Watch && state.Running is null && state.Input == TuiInputKind.None
                        && state.NextWatchRunAt is { } at && at <= now;
                }
                if (watchDue && runTask.IsCompleted) (runCts, runTask) = StartRun(state, pairCodes, ctx);

                var (width, height) = screen.Size();
                string[] frame;
                lock (state.Gate)
                {
                    state.SpinnerFrame++;
                    frame = TuiView.Render(state, term, width, height);
                }
                screen.Draw(frame, width, height);

                await Task.WhenAny(screen.Keys.WaitToReadAsync().AsTask(), Task.Delay(100)).ConfigureAwait(false);
            }
        }
        finally
        {
            runCts?.Cancel();
            lock (state.Gate) state.CancelInput();
            try { await runTask.ConfigureAwait(false); } catch { }
            screen.Dispose();
        }
        return GardApp.Ok;
    }

    private static void MergeDiscovered(TuiState state, MdnsPeerBrowser browser)
    {
        IReadOnlyCollection<DiscoveredPeer> peers;
        try
        {
            browser.Refresh();
            peers = browser.CurrentPeers;
        }
        catch
        {
            return;
        }
        lock (state.Gate)
        {
            foreach (var p in peers)
            {
                if (p.Host is null || p.Port is not { } port) continue;
                state.Upsert(new TuiPeer
                {
                    Name = p.DeviceName,
                    Host = p.Host,
                    Port = port,
                    Platform = p.Platform,
                    Caps = p.Caps,
                });
            }
        }
    }

    private static async Task ProbeAllAsync(TuiState state, CancellationToken ct)
    {
        (string Key, string Host, int Port)[] targets;
        lock (state.Gate) targets = state.Peers.Select(p => (p.Key, p.Host, p.Port)).ToArray();
        var results = await Task.WhenAll(targets.Select(async t =>
            (t.Key, Ms: await NetInfo.ProbeAsync(t.Host, t.Port, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        lock (state.Gate)
        {
            foreach (var (key, ms) in results)
            {
                if (state.Peers.FirstOrDefault(p => p.Key == key) is not { } peer) continue;
                peer.ConnectMs = ms;
                peer.Probed = true;
            }
            state.Sort();
        }
    }

    private static (CancellationTokenSource, Task) StartRun(
        TuiState state, ConcurrentDictionary<string, string> pairCodes, CliContext ctx)
    {
        TuiPeer peer;
        TestCommandOptions options;
        TuiRun run;
        lock (state.Gate)
        {
            if (state.Selected is not { } selected) return (new CancellationTokenSource(), Task.CompletedTask);
            peer = selected;
            options = state.BuildOptions(peer, pairCodes.GetValueOrDefault(peer.Key));
            run = new TuiRun { PeerName = peer.Name, Mode = state.ShortModeLabel(), TotalS = state.DurationS };
            state.Running = run;
            state.NextWatchRunAt = null;
            state.SetStatus($"measuring {peer.Name}");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Interrupted);
        var token = cts.Token;
        var sink = new TuiProgressSink(state, run);
        var task = Task.Run(async () =>
        {
            try
            {
                var result = await MeasurementRunner.RunAsync(options, sink, PairingPrompt(state, peer, pairCodes, token), token)
                    .ConfigureAwait(false);
                lock (state.Gate)
                {
                    if (result.PeerPlatform != PeerPlatform.Unknown) peer.Platform = result.PeerPlatform;
                    state.CompleteRun(result, peer.Name);
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                lock (state.Gate) state.FailRun(peer.Name, "cancelled", cancelled: true);
            }
            catch (LandspeedException ex) when (ex.Kind is LandspeedErrorKind.PairingInvalid or LandspeedErrorKind.PairingRequired)
            {
                pairCodes.TryRemove(peer.Key, out _);
                lock (state.Gate)
                {
                    state.Watch = false; // reintentar con un código malo sólo gasta el rate limit
                    state.FailRun(peer.Name, CliErrors.PairingMessage(ex));
                }
            }
            catch (Exception ex)
            {
                lock (state.Gate) state.FailRun(peer.Name, CliErrors.Describe(ex));
            }
        });
        return (cts, task);
    }

    /// <summary>
    /// Si el host exige pairing, pide el código en el pie de la TUI (una vez por
    /// host; se recuerda mientras la TUI siga abierta).
    /// </summary>
    private static Func<CancellationToken, Task<string>> PairingPrompt(
        TuiState state, TuiPeer peer, ConcurrentDictionary<string, string> pairCodes, CancellationToken token) =>
        async _ =>
        {
            if (pairCodes.TryGetValue(peer.Key, out var cached)) return cached;
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (state.Gate)
            {
                state.BeginInput(TuiInputKind.PairingCode, $"pairing code for {peer.Name}", value => tcs.TrySetResult(value));
                state.SetStatus($"{peer.Name} requires pairing: type the 6-digit code shown by its host");
            }
            await using var reg = token.Register(() =>
            {
                lock (state.Gate) state.CancelInput();
            });
            var code = (await tcs.Task.ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(code)) throw LandspeedException.PairingRequired();
            pairCodes[peer.Key] = code;
            return code;
        };

    private sealed class TuiProgressSink(TuiState state, TuiRun run) : IMeasurementProgressSink
    {
        public void SetPhase(string phase)
        {
            lock (state.Gate) run.Phase = phase;
        }

        public void OnMeasurement(MeasurementProgress progress)
        {
            lock (state.Gate)
            {
                switch (progress)
                {
                    case PingPhaseDoneProgress:
                        run.Phase = "measuring";
                        break;
                    case ThroughputTickProgress tick:
                        run.Phase = "measuring";
                        run.ElapsedS = tick.ElapsedS;
                        run.CurrentBps = tick.InstantaneousBps;
                        run.History.Add(tick.InstantaneousBps);
                        break;
                    case FinishedProgress:
                        run.Phase = "collecting results";
                        break;
                }
            }
        }
    }
}
