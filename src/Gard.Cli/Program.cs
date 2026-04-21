using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Protocol;
using Gard.Core.Transport;

const string Version = "gard 0.1.0 (LSP/1.1)";

if (args.Length == 0) { PrintUsage(Console.Out); return 1; }

var sub = args[0];
var rest = args[1..];

try
{
    return sub switch
    {
        "scan"                    => await RunScanAsync(rest),
        "host"                    => await RunHostAsync(rest),
        "test"                    => await RunTestAsync(rest),
        "-v" or "--version"       => PrintVersion(),
        "-h" or "--help" or "help"=> HelpOk(),
        _                         => Fail($"subcomando desconocido: {sub}"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
    return 2;
}

static int PrintVersion() { Console.Out.WriteLine(Version); return 0; }
static int HelpOk() { PrintUsage(Console.Out); return 0; }
static int Fail(string msg) { Console.Error.WriteLine($"gard: {msg}"); PrintUsage(Console.Error); return 2; }

static void PrintUsage(TextWriter w)
{
    w.WriteLine($"""
    {Version} — CLI de referencia del Landspeed Protocol (LSP/1.1).
    Mide throughput, latencia, jitter y pérdida en redes locales.

    uso:
      gard scan   [--seconds N]
      gard host   [--name NOMBRE] [--port N] [--dynamic-port]
      gard test   --host <ip> [--port N]
                  [--direction up|down|bidir] [--streams N]
                  [--duration S] [--warmup S] [--payload BYTES]
                  [--bidir-mode sequential|simultaneous] [--gap S]
                  [--format human|json|csv] [--label ID]
      gard test   --csv-header
      gard -v | -h

    ejemplos:
      gard scan --seconds 5
      gard host --port 7737
      gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
      gard test --host 192.168.1.50 --format csv --label corrida-a
    """);
}

// ============================================================
//  scan
// ============================================================
static async Task<int> RunScanAsync(string[] a)
{
    var seconds = GetIntOpt(a, "--seconds", 10);
    await using var browser = new MdnsPeerBrowser();
    await browser.StartAsync();

    Console.Out.WriteLine($"escaneando peers LSP en la LAN durante {seconds}s ...");
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
    Console.Out.WriteLine($"fin del escaneo. peers vigentes: {browser.CurrentPeers.Count}");
    return 0;
}

// ============================================================
//  host  (estilo iperf3)
// ============================================================
static async Task<int> RunHostAsync(string[] a)
{
    var name = GetStringOpt(a, "--name", DefaultHostName());
    var dynamicPort = HasFlag(a, "--dynamic-port");
    var port = dynamicPort ? 0 : GetIntOpt(a, "--port", DiscoveryConstants.DefaultPort);

    var listener = new TcpListener(IPAddress.IPv6Any, port);
    listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
    listener.Start();
    var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

    var identity = new DeviceIdentity
    {
        Name = name,
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = "gard-0.1.0",
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
    Println("señal recibida — cerrando");
    listener.Stop();
    Println("listo.");
    return 0;
}

static async Task HandleSessionAsync(Socket socket, DeviceIdentity identity, CancellationToken ct)
{
    var connId = ShortId();
    var remote = socket.RemoteEndPoint?.ToString() ?? "?";
    LogEvent(connId, "←", $"conexión aceptada desde {remote}");

    await using var transport = TcpFrameTransport.FromAccepted(socket);
    try
    {
        var hs = await HostHandshake.AcceptAsync(transport, identity, cancellationToken: ct);
        var sess = Short(hs.SessionId);
        LogEvent(connId, " ",
            $"handshake OK   session={sess} proto={hs.PeerVersion.Major}.{hs.PeerVersion.Minor} " +
            $"caps={hs.NegotiatedCaps.ToHexString()} clockOff={hs.ClockOffsetNs / 1_000_000}ms rtt={hs.ClockSyncRttNs / 1_000_000}ms");

        await using var router = new ControlMessageRouter(new ControlStream(transport, ct));
        router.Start();

        var started = DateTime.UtcNow;
        var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var result = await MeasurementResponder.RunAsync(
            transport, router, dataPlane, hs.SessionId, tickIntervalMs: 200, cancellationToken: ct);
        var elapsed = (DateTime.UtcNow - started).TotalSeconds;

        PrintHostTestSummary(connId, result, elapsed);
        LogEvent(connId, "→", $"conexión cerrada session={sess} (test ok)");
    }
    catch (Exception ex)
    {
        LogEvent(connId, "!", $"error: {ex.GetType().Name}: {ex.Message}");
    }
}

static void PrintHostBanner(DeviceIdentity id, int port)
{
    var rule = new string('─', 60);
    Println(rule);
    Println($" {Version}");
    Println(rule);
    Println($" host       : {id.Name}");
    Println($" plataforma : {id.Platform.ToString().ToLowerInvariant()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})");
    Println($" servicio   : _landspeed._tcp  (mDNS)");
    Println($" puerto tcp : {port}");
    var ips = LocalIPv4();
    if (ips.Count == 0) Println(" interfaces : (ninguna detectada)");
    else
    {
        Println(" interfaces :");
        foreach (var (iface, ip) in ips) Println($"              {iface}\t{ip}");
    }
    Println(rule);
    Println("escuchando — Ctrl+C para detener");
    Println("");
}

static void PrintHostTestSummary(string connId, ResultBody r, double wall)
{
    var mean = FormatMbps(r.Throughput.MeanBps);
    var peak = FormatMbps(r.Throughput.PeakBps);
    LogEvent(connId, " ", $"test finalizado dir={r.Direction.ToString().ToLowerInvariant()} streams={r.Streams} duración={r.DurationS:F2}s");
    Println(Timestamp() + "  " + connId + $"   agg  mean={mean} peak={peak}");
    if (r.ThroughputUp is { } up && r.ThroughputDown is { } down)
    {
        Println(Timestamp() + "  " + connId + $"   up   mean={FormatMbps(up.MeanBps)} peak={FormatMbps(up.PeakBps)}");
        Println(Timestamp() + "  " + connId + $"   down mean={FormatMbps(down.MeanBps)} peak={FormatMbps(down.PeakBps)}");
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
    if (HasFlag(a, "--csv-header")) { Console.Out.WriteLine(string.Join(",", CsvColumns())); return 0; }

    var host = GetStringOpt(a, "--host", "");
    if (string.IsNullOrWhiteSpace(host) && a.Length > 0 && !a[0].StartsWith("--")) host = a[0];
    if (string.IsNullOrWhiteSpace(host)) return Fail("falta --host <ip>");

    var port      = GetIntOpt(a, "--port", DiscoveryConstants.DefaultPort);
    var dirStr    = GetStringOpt(a, "--direction", GetStringOpt(a, "--dir", "down")).ToLowerInvariant();
    var streams   = GetIntOpt(a, "--streams", 1);
    var duration  = GetDoubleOpt(a, "--duration", 10);
    var warmup    = GetDoubleOpt(a, "--warmup", 1);
    var payload   = GetIntOpt(a, "--payload", 65_536);
    var bidirMStr = GetStringOpt(a, "--bidir-mode", "simultaneous").ToLowerInvariant();
    var gap       = GetDoubleOpt(a, "--gap", 0.5);
    var fmtStr    = GetStringOpt(a, "--format", "human").ToLowerInvariant();
    var label     = GetStringOpt(a, "--label", "");

    var direction = dirStr switch
    {
        "up" => TestDirection.Up, "down" => TestDirection.Down, "bidir" => TestDirection.Bidir,
        _ => throw new ArgumentException($"--direction debe ser up|down|bidir (recibido: {dirStr})"),
    };
    var bidirMode = bidirMStr switch
    {
        "simultaneous" => BidirMode.Simultaneous, "sequential" => BidirMode.Sequential,
        _ => throw new ArgumentException($"--bidir-mode debe ser sequential|simultaneous"),
    };
    if (fmtStr is not ("human" or "json" or "csv"))
        throw new ArgumentException($"--format debe ser human|json|csv (recibido: {fmtStr})");
    if (streams < 1 || streams > 32) return Fail("--streams debe estar entre 1 y 32");
    if (duration <= 0) return Fail("--duration debe ser > 0");
    if (warmup < 0) return Fail("--warmup debe ser >= 0");
    if (payload < 512) return Fail("--payload debe ser >= 512");

    Console.Error.WriteLine($"→ conectando a {host}:{port}...");
    await using var ctl = await TcpFrameTransport.ConnectAsync(host, port);
    Console.Error.WriteLine("  conexión de control lista");

    var identity = new DeviceIdentity
    {
        Name = "gard-cli",
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = "gard-0.1.0",
    };
    var hs = await ClientHandshake.PerformAsync(ctl, identity);
    Console.Error.WriteLine($"  handshake OK session={Short(hs.SessionId)} caps={hs.NegotiatedCaps.ToHexString()}");

    await using var router = new ControlMessageRouter(new ControlStream(ctl));
    router.Start();

    var parameters = new TestParameters
    {
        Direction = direction,
        DurationS = duration,
        Streams = streams,
        PayloadSize = payload,
        WarmupS = warmup,
        BidirMode = direction == TestDirection.Bidir ? bidirMode : null,
        GapS = direction == TestDirection.Bidir && bidirMode == BidirMode.Sequential ? gap : null,
    };
    var inputs = new MeasurementClientInputs
    {
        Parameters = parameters,
        PeerName = host,
        PeerPlatform = PeerPlatform.Macos,
        SessionId = hs.SessionId,
        NegotiatedCaps = hs.NegotiatedCaps,
    };

    Console.Error.WriteLine(
        $"→ corriendo test dir={direction.ToString().ToLowerInvariant()} streams={streams} " +
        $"dur={duration}s warmup={warmup}s payload={payload}...");

    var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
    var result = await MeasurementSession.RunClientAsync(
        ctl, router, dataPlane, host, inputs, onProgress: null);

    switch (fmtStr)
    {
        case "human": PrintTestHuman(result); break;
        case "json":  PrintTestJson(result); break;
        case "csv":   PrintTestCsv(result, parameters, label); break;
    }
    return 0;
}

static void PrintTestHuman(TestResult r)
{
    static string Mbps(ulong bps) => $"{bps / 1_000_000.0,8:F2} Mb/s";
    Console.Out.WriteLine("== Resultado ==");
    Console.Out.WriteLine($"  sesión    : {r.SessionId}");
    Console.Out.WriteLine($"  dirección : {r.Direction.ToString().ToLowerInvariant()}");
    Console.Out.WriteLine($"  streams   : {r.Streams}");
    Console.Out.WriteLine($"  duración  : {r.DurationS:F2} s");
    Console.Out.WriteLine($"  mean      : {Mbps(r.MeanBps)}");
    Console.Out.WriteLine($"  peak      : {Mbps(r.PeakBps)}");
    if (r.ThroughputUp   is { } up)   Console.Out.WriteLine($"  up   mean/peak: {Mbps(up.MeanBps)} / {Mbps(up.PeakBps)}");
    if (r.ThroughputDown is { } down) Console.Out.WriteLine($"  down mean/peak: {Mbps(down.MeanBps)} / {Mbps(down.PeakBps)}");
    Console.Out.WriteLine($"  ping      : min={r.PingMinMs:F2} avg={r.PingAvgMs:F2} max={r.PingMaxMs:F2} p95={r.PingP95Ms:F2} ms");
    Console.Out.WriteLine($"  jitter    : {r.JitterMs:F2} ms");
    Console.Out.WriteLine($"  loss      : {r.LossPct:F2} % (n={r.PingSamples})");
    if (r.RttUnderLoadMs is { } rtt)
        Console.Out.WriteLine($"  rtt load  : median={rtt.MedianMs:F2} p95={rtt.P95Ms:F2} spikes={rtt.SpikesCount} (n={rtt.Samples})");
}

static void PrintTestJson(TestResult r)
{
    var opts = new JsonSerializerOptions { WriteIndented = false };
    Console.Out.WriteLine(JsonSerializer.Serialize(r, opts));
}

static string[] CsvColumns() =>
[
    "label","direction","streams","duration_s","warmup_s","payload","bidir_mode",
    "mean_mbps","peak_mbps",
    "up_mean_mbps","up_peak_mbps","down_mean_mbps","down_peak_mbps",
    "ping_min_ms","ping_avg_ms","ping_max_ms","ping_p95_ms",
    "jitter_ms","loss_pct","ping_samples",
    "rtt_load_median_ms","rtt_load_p95_ms","rtt_load_spikes",
];

static void PrintTestCsv(TestResult r, TestParameters p, string label)
{
    static string Mbps(ulong bps) => (bps / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture);
    static string F(double v)     => v.ToString("F3", CultureInfo.InvariantCulture);
    string upMean   = r.ThroughputUp   is { } u  ? Mbps(u.MeanBps) : "";
    string upPeak   = r.ThroughputUp   is { } u2 ? Mbps(u2.PeakBps) : "";
    string downMean = r.ThroughputDown is { } d  ? Mbps(d.MeanBps) : "";
    string downPeak = r.ThroughputDown is { } d2 ? Mbps(d2.PeakBps) : "";
    string rttMed   = r.RttUnderLoadMs is { } m  ? F(m.MedianMs) : "";
    string rttP95   = r.RttUnderLoadMs is { } m2 ? F(m2.P95Ms) : "";
    string rttSpk   = r.RttUnderLoadMs is { } m3 ? m3.SpikesCount.ToString(CultureInfo.InvariantCulture) : "";

    var cells = new[]
    {
        label,
        r.Direction.ToString().ToLowerInvariant(),
        r.Streams.ToString(CultureInfo.InvariantCulture),
        r.DurationS.ToString("F2", CultureInfo.InvariantCulture),
        p.WarmupS.ToString("F2", CultureInfo.InvariantCulture),
        p.PayloadSize.ToString(CultureInfo.InvariantCulture),
        p.BidirMode?.ToString().ToLowerInvariant() ?? "",
        Mbps(r.MeanBps), Mbps(r.PeakBps),
        upMean, upPeak, downMean, downPeak,
        F(r.PingMinMs), F(r.PingAvgMs), F(r.PingMaxMs), F(r.PingP95Ms),
        F(r.JitterMs), F(r.LossPct), r.PingSamples.ToString(CultureInfo.InvariantCulture),
        rttMed, rttP95, rttSpk,
    };
    Console.Out.WriteLine(string.Join(",", cells));
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
static string FormatMbps(ulong bps) => $"{bps / 1_000_000.0,7:F2} Mb/s";
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

static bool HasFlag(string[] a, string name)
{
    for (int i = 0; i < a.Length; i++) if (a[i] == name) return true;
    return false;
}
static string GetStringOpt(string[] a, string name, string def)
{
    for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return def;
}
static int GetIntOpt(string[] a, string name, int def)
    => int.TryParse(GetStringOpt(a, name, def.ToString(CultureInfo.InvariantCulture)),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
static double GetDoubleOpt(string[] a, string name, double def)
    => double.TryParse(GetStringOpt(a, name, def.ToString(CultureInfo.InvariantCulture)),
                       NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
