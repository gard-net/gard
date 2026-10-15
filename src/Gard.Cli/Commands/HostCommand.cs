using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Cli;

public static class HostCommand
{
    public static async Task<int> RunAsync(string[] args, CliContext ctx)
    {
        var options = CliOptions.ParseHost(args, NetInfo.DefaultHostName());
        using var timerRes = WindowsTimerResolution.RaiseToOneMs();

        var listener = new TcpListener(IPAddress.IPv6Any, options.Port);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var identity = new DeviceIdentity
        {
            Name = options.Name,
            Platform = DeviceIdentity.CurrentPlatform,
            AppVersion = GardCliInfo.AppVersion,
        };
        await using var advertiser = new MdnsPeerAdvertiser(identity, port: port);
        await advertiser.StartAsync().ConfigureAwait(false);

        // Sin pairing cualquier peer de la LAN puede pedir tests (spec §7).
        CodePairingValidator? pairing = options.RequirePairing
            ? new CodePairingValidator(options.PairCode ?? CodePairingValidator.GenerateCode())
            : null;
        var log = new HostLog(ctx);
        PrintBanner(ctx, identity, port, pairing);

        // Tope de sesiones simultáneas: cada una reserva sockets, hilos y buffers.
        // Sin `using`: las sesiones en vuelo hacen Release después de retornar.
        var slots = new SemaphoreSlim(GardCliInfo.MaxConcurrentHostSessions);
        while (!ctx.Interrupted.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(ctx.Interrupted).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!slots.Wait(0))
            {
                log.Fail(HostLog.NewId(), $"rejected {NetInfo.Endpoint(socket.RemoteEndPoint)}: {GardCliInfo.MaxConcurrentHostSessions} sessions already active");
                socket.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleSessionAsync(socket, identity, pairing, log, ctx.Interrupted).ConfigureAwait(false); }
                finally { slots.Release(); }
            });
        }

        listener.Stop();
        ctx.Out.WriteLine();
        ctx.Out.WriteLine(ctx.Term.Dim("host stopped"));
        return GardApp.Ok;
    }

    private static void PrintBanner(CliContext ctx, DeviceIdentity id, int port, CodePairingValidator? pairing)
    {
        var t = ctx.Term;
        var s = t.Symbols;
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var addresses = NetInfo.LocalIPv4();

        ctx.Out.WriteLine($"{t.Bold("gard host")} {t.Dim(s.Dot)} {t.Cyan(id.Name)}  {t.Dim(GardCliInfo.VersionString)}");
        Row("listening", $"tcp/{port} {t.Dim($"{s.Dot} mDNS {DiscoveryConstants.ServiceType}")}");
        Row("platform", $"{id.Platform.ToString().ToLowerInvariant()} {arch}");
        Row("pairing", pairing is null
            ? $"{t.Yellow("off")} {t.Dim($"{s.Dot} any LAN peer can run tests; start with --pair to require a code")}"
            : $"{t.Green("required")} {t.Dim(s.Dot)} code {t.Bold(pairing.Code)}");
        Row("addresses", addresses.Count == 0
            ? t.Dim("no IPv4 interfaces up")
            : string.Join(", ", addresses.Select(a => $"{a.Ip} {t.Dim($"({a.Iface})")}")));
        if (addresses.FirstOrDefault() is { Ip: { } ip })
            Row("try", t.Dim($"gard test {ip}" + (port == DiscoveryConstants.DefaultPort ? "" : $" -p {port}") +
                (pairing is null ? "" : $" --pair-code {pairing.Code}")));
        ctx.Out.WriteLine();
        ctx.Out.WriteLine(t.Dim($"waiting for peers {s.Dot} Ctrl+C to stop"));
        ctx.Out.Flush();

        void Row(string label, string value) => ctx.Out.WriteLine($"  {t.Dim(label.PadRight(10))} {value}");
    }

    private static async Task HandleSessionAsync(
        Socket socket, DeviceIdentity identity, IPairingValidator? pairing, HostLog log, CancellationToken ct)
    {
        var id = HostLog.NewId();
        var remote = NetInfo.Endpoint(socket.RemoteEndPoint);
        log.Info(id, $"connection from {remote}");

        await using var transport = TcpFrameTransport.FromAccepted(socket);
        var handshakeDone = false;
        try
        {
            var hs = await HostHandshake.AcceptAsync(
                transport, identity, pairing: pairing, cancellationToken: ct,
                remoteAddress: (socket.RemoteEndPoint as IPEndPoint)?.Address).ConfigureAwait(false);
            handshakeDone = true;
            log.Info(id, $"handshake LSP/{hs.PeerVersion} {hs.PeerPlatform.ToString().ToLowerInvariant()} caps {hs.NegotiatedCaps.ToHexString()}");

            await using var router = new ControlMessageRouter(new ControlStream(transport, ct));
            router.Start();
            var result = await MeasurementResponder.RunAsync(
                transport, router, new TcpDataPlane { BindAddress = IPAddress.IPv6Any }, hs.SessionId,
                tickIntervalMs: 200, negotiatedCaps: hs.NegotiatedCaps, cancellationToken: ct).ConfigureAwait(false);

            var quality = CliQuality.Grade(result);
            log.Ok(id,
                $"{result.Direction.ToString().ToLowerInvariant()} {result.Streams}{(log.Term.Unicode ? "×" : "x")}{(result.Udp is null ? "tcp" : "udp")} " +
                $"{result.DurationS.ToString("F1", CultureInfo.InvariantCulture)}s  " +
                $"{CliHumanFormatter.FormatBps(result.Throughput.MeanBps)} mean  " +
                CliQuality.Paint(log.Term, quality));
        }
        catch (LandspeedException ex) when (ex.Kind is LandspeedErrorKind.PairingInvalid
            or LandspeedErrorKind.PairingRequired or LandspeedErrorKind.PairingRateLimited)
        {
            log.Fail(id, $"pairing rejected ({ex.Kind switch
            {
                LandspeedErrorKind.PairingRateLimited => "too many failed attempts",
                LandspeedErrorKind.PairingRequired => "no code sent",
                _ => "wrong code",
            }})");
        }
        catch (LandspeedException ex) when (!handshakeDone && ex.Context.ContainsKey("closed"))
        {
            // Connect y cierre sin hello: la sonda de `gard scan` u otro chequeo de puerto.
            log.Info(id, "closed before handshake (reachability probe)");
        }
        catch (LandspeedException ex) when (ex.Kind == LandspeedErrorKind.PeerClosedWithoutTest)
        {
            // `gard info` y sondeos: handshake y cierre, sin test. No es un error.
            log.Info(id, "closed by peer (no test)");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            log.Info(id, "aborted: host stopping");
        }
        catch (Exception ex)
        {
            log.Fail(id, CliErrors.Describe(ex));
        }
    }
}

/// <summary>Log de eventos del host: <c>14:05:32  dzvy  ✓ down 4×tcp …</c>, serializado entre sesiones.</summary>
internal sealed class HostLog(CliContext ctx)
{
    private readonly object _gate = new();
    public CliTerminal Term => ctx.Term;

    public void Info(string id, string msg) => Write(id, Term.Dim(Term.Symbols.Dot), msg);
    public void Ok(string id, string msg) => Write(id, Term.Green(Term.Symbols.Ok), msg);
    public void Fail(string id, string msg) => Write(id, Term.Red(Term.Symbols.Fail), msg);

    private void Write(string id, string mark, string msg)
    {
        lock (_gate)
        {
            ctx.Out.WriteLine($"{Term.Dim(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}  {Term.Cyan(id)}  {mark} {msg}");
            ctx.Out.Flush();
        }
    }

    public static string NewId()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        Span<char> buf = stackalloc char[4];
        for (var i = 0; i < 4; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }
}
