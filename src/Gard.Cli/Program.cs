using System.Net;
using System.Net.Sockets;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var sub = args[0];
var rest = args[1..];
try
{
    return sub switch
    {
        "scan" => await RunScanAsync(rest),
        "host" => await RunHostAsync(rest),
        "test" => await RunTestAsync(rest),
        "--help" or "-h" or "help" => Usage(),
        _ => Usage($"subcomando desconocido: {sub}"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

static int Usage(string? err = null)
{
    if (err is not null) Console.Error.WriteLine($"error: {err}\n");
    PrintUsage();
    return err is null ? 0 : 1;
}

static void PrintUsage()
{
    Console.Out.WriteLine("""
    gard — reference CLI for the Landspeed Protocol (LSP/1.1).
    Measures throughput, latency, jitter and packet loss on a local network.

    usage:
      gard scan   [--seconds N]
      gard host   [--port P] [--name NAME]
      gard test   <host> [--port P] [--dir up|down|bidir]
                         [--duration S] [--streams N] [--payload BYTES]

    examples:
      gard scan --seconds 5
      gard host --port 7737
      gard test 192.168.1.50 --dir down --duration 10 --streams 4
    """);
}

static async Task<int> RunScanAsync(string[] args)
{
    var seconds = GetIntOption(args, "--seconds", 10);
    await using var browser = new MdnsPeerBrowser();
    await browser.StartAsync();

    Console.Out.WriteLine($"scanning LSP peers on LAN for {seconds}s ...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    try
    {
        await foreach (var e in browser.Events.ReadAllAsync(cts.Token))
        {
            switch (e.Kind)
            {
                case PeerEvent.EventKind.Added when e.Peer is { } p:
                    Console.Out.WriteLine($"+ {p.DeviceName} [{p.Platform}] {p.Host}:{p.Port} caps={p.Caps.ToHexString()} v={p.ProtocolVersionMajor}");
                    break;
                case PeerEvent.EventKind.Updated when e.Peer is { } p:
                    Console.Out.WriteLine($"~ {p.DeviceName} {p.Host}:{p.Port}");
                    break;
                case PeerEvent.EventKind.Removed:
                    Console.Out.WriteLine($"- {e.ServiceInstanceName}");
                    break;
            }
        }
    }
    catch (OperationCanceledException) { }
    Console.Out.WriteLine($"fin del escaneo. Peers actuales: {browser.CurrentPeers.Count}");
    return 0;
}

static async Task<int> RunHostAsync(string[] args)
{
    var port = GetIntOption(args, "--port", DiscoveryConstants.DefaultPort);
    var name = GetStringOption(args, "--name", Dns.GetHostName());

    var listener = new TcpListener(IPAddress.IPv6Any, port);
    listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
    listener.Start();
    var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

    var identity = new DeviceIdentity
    {
        Name = name,
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = "0.1.0-cli",
    };
    await using var advertiser = new MdnsPeerAdvertiser(identity, port: actualPort);
    await advertiser.StartAsync();

    Console.Out.WriteLine($"host listo en *:{actualPort} (mDNS: {advertiser.FullyQualifiedName}). Ctrl-C para salir.");
    using var shutdownCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; shutdownCts.Cancel(); };

    while (!shutdownCts.IsCancellationRequested)
    {
        Socket ctl;
        try
        {
            ctl = await listener.AcceptSocketAsync(shutdownCts.Token);
        }
        catch (OperationCanceledException) { break; }

        _ = Task.Run(() => HandleSessionAsync(ctl, identity, shutdownCts.Token));
    }
    listener.Stop();
    return 0;
}

static async Task HandleSessionAsync(Socket socket, DeviceIdentity identity, CancellationToken ct)
{
    var remote = socket.RemoteEndPoint?.ToString() ?? "?";
    Console.Out.WriteLine($"[{remote}] conexión aceptada");
    await using var transport = TcpFrameTransport.FromAccepted(socket);
    try
    {
        var hs = await HostHandshake.AcceptAsync(transport, identity, cancellationToken: ct);
        Console.Out.WriteLine($"[{remote}] handshake ok session={hs.SessionId} caps={hs.NegotiatedCaps.ToHexString()} clockOffset={hs.ClockOffsetNs}ns");

        await using var router = new ControlMessageRouter(new ControlStream(transport, ct));
        router.Start();

        var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var result = await MeasurementResponder.RunAsync(
            transport, router, dataPlane, hs.SessionId, tickIntervalMs: 200, cancellationToken: ct);

        var fmt = new BpsFormatter(ThroughputUnit.Mbps);
        Console.Out.WriteLine($"[{remote}] test ok mean={fmt.FormatCompact(result.Throughput.MeanBps)} peak={fmt.FormatCompact(result.Throughput.PeakBps)} dur={result.DurationS:F2}s");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{remote}] error: {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task<int> RunTestAsync(string[] args)
{
    if (args.Length == 0) { PrintUsage(); Console.Error.WriteLine("falta <host>"); return 1; }
    var host = args[0];
    var rest = args[1..];

    var port = GetIntOption(rest, "--port", DiscoveryConstants.DefaultPort);
    var dirStr = GetStringOption(rest, "--dir", "down").ToLowerInvariant();
    var duration = GetDoubleOption(rest, "--duration", 5.0);
    var streams = GetIntOption(rest, "--streams", 4);
    var payload = GetIntOption(rest, "--payload", 65_536);

    var direction = dirStr switch
    {
        "up" => TestDirection.Up,
        "down" => TestDirection.Down,
        "bidir" => TestDirection.Bidir,
        _ => throw new ArgumentException($"dirección desconocida: {dirStr}"),
    };

    Console.Out.WriteLine($"conectando a {host}:{port} ...");
    await using var ctl = await TcpFrameTransport.ConnectAsync(host, port);

    var identity = new DeviceIdentity
    {
        Name = Dns.GetHostName(),
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = "0.1.0-cli",
    };
    var hs = await ClientHandshake.PerformAsync(ctl, identity);
    Console.Out.WriteLine($"handshake ok session={hs.SessionId} caps={hs.NegotiatedCaps.ToHexString()} peer={hs.PeerVersion}");

    await using var router = new ControlMessageRouter(new ControlStream(ctl));
    router.Start();

    var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
    var inputs = new MeasurementClientInputs
    {
        Parameters = new TestParameters
        {
            Direction = direction,
            DurationS = duration,
            Streams = streams,
            PayloadSize = payload,
            WarmupS = 0.5,
        },
        PeerName = host,
        PeerPlatform = PeerPlatform.Macos,
        SessionId = hs.SessionId,
        NegotiatedCaps = hs.NegotiatedCaps,
    };

    var fmt = new BpsFormatter(ThroughputUnit.Mbps);
    var result = await MeasurementSession.RunClientAsync(
        ctl, router, dataPlane, host, inputs,
        onProgress: p =>
        {
            if (p is ThroughputTickProgress tick)
            {
                Console.Out.WriteLine($"  t={tick.ElapsedS:F1}s inst={fmt.FormatCompact(tick.InstantaneousBps)}");
            }
        });

    Console.Out.WriteLine();
    Console.Out.WriteLine("resultado:");
    Console.Out.WriteLine($"  session     {result.SessionId}");
    Console.Out.WriteLine($"  dirección   {result.Direction}");
    Console.Out.WriteLine($"  streams     {result.Streams}");
    Console.Out.WriteLine($"  duración    {result.DurationS:F2}s");
    Console.Out.WriteLine($"  mean        {fmt.FormatCompact(result.MeanBps)}");
    Console.Out.WriteLine($"  peak        {fmt.FormatCompact(result.PeakBps)}");
    Console.Out.WriteLine($"  per-stream  {string.Join(", ", result.PerStreamBps.Select(b => fmt.FormatCompact(b)))}");
    Console.Out.WriteLine($"  ping        avg={result.PingAvgMs:F2}ms p95={result.PingP95Ms:F2}ms loss={result.LossPct:F1}% jitter={result.JitterMs:F2}ms");
    return 0;
}

static string GetStringOption(string[] args, string name, string def)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) return args[i + 1];
    }
    return def;
}

static int GetIntOption(string[] args, string name, int def)
    => int.TryParse(GetStringOption(args, name, def.ToString()), out var v) ? v : def;

static double GetDoubleOption(string[] args, string name, double def)
    => double.TryParse(GetStringOption(args, name, def.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
