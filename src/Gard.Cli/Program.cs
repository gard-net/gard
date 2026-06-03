using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gard.Cli;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;
using Gard.Core.Utils;

if (args.Length == 0) { Console.Out.WriteLine(CliHelp.General()); return 1; }

var sub = args[0];
var rest = args[1..];
var activeCommand = sub is "scan" or "host" or "test" ? sub : null;

try
{
    return sub switch
    {
        "help"                    => Help(rest),
        "scan"                    => await RunScanAsync(rest),
        "host"                    => await RunHostAsync(rest),
        "test"                    => await RunTestAsync(rest),
        "-v" or "--version"       => PrintVersion(),
        "-h" or "--help"          => HelpOk(),
        _                         => Fail($"unknown command: {sub}", null),
    };
}
catch (CliOptionException ex)
{
    Console.Error.WriteLine($"gard: {ex.Message}");
    Console.Error.WriteLine(CliErrors.HintFor(activeCommand));
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"gard: {ex.Message}");
    Console.Error.WriteLine(CliErrors.HintFor(activeCommand));
    return 2;
}

static int PrintVersion() { Console.Out.WriteLine(GardCliInfo.VersionString); return 0; }
static int HelpOk() { Console.Out.WriteLine(CliHelp.General()); return 0; }
static int Help(string[] a)
{
    Console.Out.WriteLine(a.Length > 0 ? CliHelp.ForCommand(a[0]) : CliHelp.General());
    return 0;
}
static int Fail(string msg, string? command) { Console.Error.WriteLine($"gard: {msg}"); Console.Error.WriteLine(CliErrors.HintFor(command)); return 2; }

// ============================================================
//  scan
// ============================================================
static async Task<int> RunScanAsync(string[] a)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Scan()); return 0; }
    var options = CliOptions.ParseScan(a);
    await using var browser = new MdnsPeerBrowser();
    await browser.StartAsync();

    Console.Out.WriteLine($"Scanning for LSP peers for {options.Seconds}s...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
    try
    {
        await foreach (var e in browser.Events.ReadAllAsync(cts.Token))
        {
            switch (e.Kind)
            {
                case PeerEvent.EventKind.Added when e.Peer is { } p:
                    Console.Out.WriteLine($"+ {p.DeviceName,-24} {p.Platform.ToString().ToLowerInvariant(),-8} {p.Host}:{p.Port}  caps={p.Caps.ToHexString()}  lsp={p.ProtocolVersionMajor}.x");
                    break;
                case PeerEvent.EventKind.Updated when e.Peer is { } p:
                    Console.Out.WriteLine($"~ {p.DeviceName,-24} {p.Host}:{p.Port}");
                    break;
                case PeerEvent.EventKind.Removed:
                    Console.Out.WriteLine($"- {e.ServiceInstanceName}");
                    break;
            }
        }
    }
    catch (OperationCanceledException) { }
    Console.Out.WriteLine($"Done. Active peers: {browser.CurrentPeers.Count}");
    return 0;
}

// ============================================================
//  host  (estilo iperf3)
// ============================================================
static async Task<int> RunHostAsync(string[] a)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Host()); return 0; }
    var options = CliOptions.ParseHost(a, DefaultHostName());

    // En Windows el scheduler está en ~15.6 ms por default; baja a 1 ms
    // para que las continuations del sender UDP no se alineen a ticks
    // gruesas. No-op en macOS/Linux.
    using var timerRes = WindowsTimerResolution.RaiseToOneMs();

    var listener = new TcpListener(IPAddress.IPv6Any, options.Port);
    listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
    listener.Start();
    var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

    var identity = new DeviceIdentity
    {
        Name = options.Name,
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = GardCliInfo.AppVersion,
    };
    await using var advertiser = new MdnsPeerAdvertiser(identity, port: actualPort);
    await advertiser.StartAsync();

    PrintHostBanner(identity, actualPort);

    using var shutdownCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; shutdownCts.Cancel(); };

    while (!shutdownCts.IsCancellationRequested)
    {
        Socket ctl;
        try { ctl = await listener.AcceptSocketAsync(shutdownCts.Token); }
        catch (OperationCanceledException) { break; }
        _ = Task.Run(() => HandleSessionAsync(ctl, identity, shutdownCts.Token));
    }

    Println("");
    Println("Shutdown requested. Closing listener...");
    listener.Stop();
    Println("Done.");
    return 0;
}

static async Task HandleSessionAsync(Socket socket, DeviceIdentity identity, CancellationToken ct)
{
    var connId = ShortId();
    var remote = socket.RemoteEndPoint?.ToString() ?? "?";
    LogEvent(connId, "in", $"accepted connection from {remote}");

    await using var transport = TcpFrameTransport.FromAccepted(socket);
    try
    {
        var hs = await HostHandshake.AcceptAsync(transport, identity, cancellationToken: ct);
        var sess = Short(hs.SessionId);
        LogEvent(connId, " ",
            $"handshake ok   session={sess} lsp={hs.PeerVersion.Major}.{hs.PeerVersion.Minor} " +
            $"caps={hs.NegotiatedCaps.ToHexString()} clockOff={hs.ClockOffsetNs / 1_000_000}ms rtt={hs.ClockSyncRttNs / 1_000_000}ms");

        await using var router = new ControlMessageRouter(new ControlStream(transport, ct));
        router.Start();

        var started = DateTime.UtcNow;
        var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var result = await MeasurementResponder.RunAsync(
            transport, router, dataPlane, hs.SessionId, tickIntervalMs: 200, cancellationToken: ct);
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;

        PrintHostTestSummary(connId, result, elapsed);
        LogEvent(connId, "ok", $"closed session={sess}");
    }
    catch (Exception ex)
    {
        LogEvent(connId, "!!", $"error: {ex.Message}");
    }
}

static void PrintHostBanner(DeviceIdentity id, int port)
{
    var rule = new string('-', 60);
    Println(rule);
    Println($" {GardCliInfo.VersionString}");
    Println(rule);
    Println($" name       {id.Name}");
    Println($" platform   {id.Platform.ToString().ToLowerInvariant()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})");
    Println($" service    _landspeed._tcp (mDNS)");
    Println($" tcp port   {port}");
    var ips = LocalIPv4();
    if (ips.Count == 0) Println(" interfaces none detected");
    else
    {
        Println(" interfaces");
        foreach (var (iface, ip) in ips) Println($"            {iface,-12} {ip}");
    }
    Println(rule);
    Println("Listening. Press Ctrl+C to stop.");
    Println("");
}

static void PrintHostTestSummary(string connId, ResultBody r, double wall)
{
    LogEvent(connId, "ok", $"test complete dir={r.Direction.ToString().ToLowerInvariant()} streams={r.Streams} duration={r.DurationS:F2}s");
    Println(Timestamp() + "  " + connId + $"    {CliHumanFormatter.FormatResultBodySummary(r)}");
    if (r.ThroughputUp is { } up && r.ThroughputDown is { } down)
    {
        Println(Timestamp() + "  " + connId + $"    up   mean={CliHumanFormatter.FormatBps(up.MeanBps)} peak={CliHumanFormatter.FormatBps(up.PeakBps)}");
        Println(Timestamp() + "  " + connId + $"    down mean={CliHumanFormatter.FormatBps(down.MeanBps)} peak={CliHumanFormatter.FormatBps(down.PeakBps)}");
    }
    if (r.Samples > 0)
    {
        Println(Timestamp() + "  " + connId +
            $"   ping avg={r.LatencyMs.Avg:F1}ms p95={r.LatencyMs.P95:F1}ms jit={r.JitterMs:F1}ms loss={r.LossPct:F1}% (n={r.Samples})");
    }
    if (r.RttUnderLoad is { } rtt && rtt.Samples > 0)
    {
        Println(Timestamp() + "  " + connId +
            $"   rtt load med={rtt.MedianMs:F1}ms p95={rtt.P95Ms:F1}ms spikes={rtt.SpikesCount} (n={rtt.Samples})");
    }
}

// ============================================================
//  test  (estilo LandspeedRunner)
// ============================================================
static async Task<int> RunTestAsync(string[] a)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Test()); return 0; }
    if (CliOptions.HasFlag(a, "--csv-header")) { Console.Out.WriteLine(CliCsvFormatter.Header()); return 0; }

    var options = CliOptions.ParseTest(a);
    var parameters = options.Parameters;

    // En Windows el scheduler está en ~15.6 ms por default; baja a 1 ms
    // para que el UdpSender del cliente (direction up/bidir) no quede
    // techado por ticks gruesas. No-op en macOS/Linux.
    using var timerRes = WindowsTimerResolution.RaiseToOneMs();

    Console.Error.WriteLine($"Connecting to {options.Host}:{options.Port}...");
    await using var ctl = await TcpFrameTransport.ConnectAsync(options.Host, options.Port);
    Console.Error.WriteLine("Control channel ready.");

    var identity = new DeviceIdentity
    {
        Name = "gard-cli",
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = GardCliInfo.AppVersion,
    };
    var hs = await ClientHandshake.PerformAsync(ctl, identity);
    Console.Error.WriteLine($"Handshake ok. session={Short(hs.SessionId)} caps={hs.NegotiatedCaps.ToHexString()}");

    await using var router = new ControlMessageRouter(new ControlStream(ctl));
    router.Start();

    var inputs = new MeasurementClientInputs
    {
        Parameters = parameters,
        PeerName = options.Host,
        PeerPlatform = DeviceIdentity.CurrentPlatform,
        SessionId = hs.SessionId,
        NegotiatedCaps = hs.NegotiatedCaps,
    };

    Console.Error.WriteLine(
        $"Running {parameters.Transport.ToString().ToLowerInvariant()} {parameters.Direction.ToString().ToLowerInvariant()} test: " +
        $"streams={parameters.Streams} duration={parameters.DurationS}s warmup={parameters.WarmupS}s payload={parameters.PayloadSize}.");

    var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
    var result = await MeasurementSession.RunClientAsync(
        ctl, router, dataPlane, options.Host, inputs, onProgress: null);

    switch (options.Format)
    {
        case "human": Console.Out.WriteLine(CliHumanFormatter.Format(result)); break;
        case "json":  PrintTestJson(result); break;
        case "csv":   Console.Out.WriteLine(CliCsvFormatter.Format(result, parameters, options.Label)); break;
    }
    return 0;
}

static void PrintTestJson(TestResult r)
{
    var opts = new JsonSerializerOptions { WriteIndented = false };
    Console.Out.WriteLine(JsonSerializer.Serialize(r, opts));
}

// ============================================================
//  helpers
// ============================================================
static string Timestamp() => $"[{DateTime.Now:HH:mm:ss}]";
static string ShortId()
{
    const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
    var rng = Random.Shared;
    Span<char> buf = stackalloc char[4];
    for (int i = 0; i < 4; i++) buf[i] = chars[rng.Next(chars.Length)];
    return new string(buf);
}
static string Short(string s) => s.Length > 8 ? s[..8] : s;
static void LogEvent(string connId, string arrow, string msg)
{
    Console.Out.WriteLine($"{Timestamp()}  {connId}  {arrow} {msg}");
    Console.Out.Flush();
}
static void Println(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

static string DefaultHostName()
{
    var h = Dns.GetHostName();
    return h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? h[..^6] : h;
}

static List<(string Iface, string Ip)> LocalIPv4()
{
    var result = new List<(string, string)>();
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            var ip = ua.Address.ToString();
            if (ip.StartsWith("169.254.")) continue;
            result.Add((ni.Name, ip));
        }
    }
    return result;
}
