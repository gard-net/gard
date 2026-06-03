using System.Diagnostics;
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

var style = CliOptions.ParseGlobalStyle(args);
var term = new CliTerminal(style);
var cleanArgs = CliOptions.StripGlobalOptions(args);
if (cleanArgs.Length == 0) { Console.Out.WriteLine(CliHelp.General()); return 1; }

var sub = cleanArgs[0];
var rest = cleanArgs[1..];
var activeCommand = sub is "scan" or "host" or "test" or "watch" or "info" or "doctor" ? sub : null;

try
{
    return sub switch
    {
        "help"                    => Help(rest),
        "scan"                    => await RunScanAsync(rest, term),
        "host"                    => await RunHostAsync(rest, term),
        "test"                    => await RunTestAsync(rest, term),
        "watch"                   => await RunWatchAsync(rest, term),
        "info"                    => await RunInfoAsync(rest, term),
        "doctor"                  => RunDoctor(rest, term),
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

static async Task<int> RunScanAsync(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Scan()); return 0; }
    var options = CliOptions.ParseScan(a);
    await using var browser = new MdnsPeerBrowser();
    await browser.StartAsync();

    Console.Out.WriteLine(term.Rich ? $"{term.Green(">")} gard scan" : $"Scanning for LSP peers for {options.Seconds}s...");
    if (term.Rich)
    {
        Console.Out.WriteLine(term.Cyan($"Scanning local network using LSP/1.2 for {options.Seconds}s..."));
        var glyph = CliPixelArt.ScanGlyph(term);
        if (glyph.Length > 0) Console.Out.WriteLine(glyph);
        Console.Out.WriteLine();
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
    var refreshTask = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                browser.Refresh();
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    });
    try { await foreach (var _ in browser.Events.ReadAllAsync(cts.Token)) { } }
    catch (OperationCanceledException) { }
    await refreshTask;

    var peers = browser.CurrentPeers
        .OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase)
        .Select(p => (IReadOnlyList<string>)[
            p.DeviceName,
            $"{p.Host}:{p.Port}",
            p.Platform.ToString().ToLowerInvariant(),
            $"LSP/{p.ProtocolVersionMajor}.x",
            "n/a",
            term.Green("ready"),
        ])
        .ToList();

    if (term.Rich && peers.Count > 0)
    {
        Console.Out.WriteLine(CliTable.Render(term, [
            new("NAME", 18),
            new("ADDRESS", 22),
            new("OS", 8),
            new("PROTOCOL", 10),
            new("LATENCY", 8, Right: true),
            new("STATUS", 10),
        ], peers));
    }
    else if (peers.Count > 0)
    {
        foreach (var p in browser.CurrentPeers)
            Console.Out.WriteLine($"+ {p.DeviceName,-24} {p.Platform.ToString().ToLowerInvariant(),-8} {p.Host}:{p.Port}  caps={p.Caps.ToHexString()}  lsp={p.ProtocolVersionMajor}.x");
    }

    Console.Out.WriteLine(term.Cyan($"{browser.CurrentPeers.Count} device{(browser.CurrentPeers.Count == 1 ? "" : "s")} found."));
    return 0;
}

static async Task<int> RunHostAsync(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Host()); return 0; }
    var options = CliOptions.ParseHost(a, DefaultHostName());
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

    PrintHostBanner(identity, actualPort, term);

    using var shutdownCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; shutdownCts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownCts.Cancel();

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

static async Task<int> RunTestAsync(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Test()); return 0; }
    if (CliOptions.HasFlag(a, "--csv-header")) { Console.Out.WriteLine(CliCsvFormatter.Header()); return 0; }

    var options = CliOptions.ParseTest(a);
    var human = options.Format == "human";
    var result = await RunMeasurementAsync(options, human ? term : new CliTerminal(new CliStyleOptions(true, true, "plain")), human);

    switch (options.Format)
    {
        case "human": Console.Out.WriteLine(CliHumanFormatter.Format(result, term)); break;
        case "json":  PrintTestJson(result); break;
        case "csv":   Console.Out.WriteLine(CliCsvFormatter.Format(result, options.Parameters, options.Label)); break;
    }
    return 0;
}

static async Task<int> RunInfoAsync(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Info()); return 0; }
    var options = CliOptions.ParseInfo(a);
    Console.Error.WriteLine($"Connecting to {options.Host}:{options.Port}...");
    await using var ctl = await TcpFrameTransport.ConnectAsync(options.Host, options.Port);
    var identity = ClientIdentity();
    var hs = await ClientHandshake.PerformAsync(ctl, identity);

    var rows = new List<IReadOnlyList<string>>
    {
        new[] { "host", options.Host },
        new[] { "port", options.Port.ToString() },
        new[] { "protocol", $"LSP/{hs.PeerVersion}" },
        new[] { "session", Short(hs.SessionId) },
        new[] { "caps", hs.NegotiatedCaps.ToHexString() },
        new[] { "transports", hs.NegotiatedCaps.HasFlag(Capabilities.UdpDataPlane) ? "tcp, udp" : "tcp" },
        new[] { "clock rtt", $"{hs.ClockSyncRttNs / 1_000_000.0:F2} ms" },
    };

    if (term.Rich)
    {
        Console.Out.WriteLine($"{term.Green(">")} gard info {term.Cyan(options.Host)}");
        Console.Out.WriteLine(CliTable.Render(term, [new("FIELD", 14), new("VALUE", 42)], rows));
    }
    else
    {
        Console.Out.WriteLine($"Gard peer info: {options.Host}:{options.Port}");
        foreach (var row in rows) Console.Out.WriteLine($"{row[0],-12} {row[1]}");
    }
    return 0;
}

static async Task<int> RunWatchAsync(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Watch()); return 0; }
    var options = CliOptions.ParseWatch(a);
    if (options.Test.Format is not "human") throw new CliOptionException("watch supports human output only");

    using var shutdownCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; shutdownCts.Cancel(); };

    Console.Out.WriteLine(term.Rich
        ? $"{term.Green(">")} gard watch {term.Cyan(options.Host)} {term.Dim("--interval " + options.IntervalS.ToString("F0") + "s")}"
        : $"Watching {options.Host}. Press Ctrl+C to stop.");
    Console.Out.WriteLine("Press Ctrl+C to stop.");
    Console.Out.WriteLine();

    var columns = new[]
    {
        new CliTableColumn("TIME", 8),
        new CliTableColumn("DOWN (Mbps)", 12, Right: true),
        new CliTableColumn("UP (Mbps)", 10, Right: true),
        new CliTableColumn("LATENCY", 10, Right: true),
        new CliTableColumn("LOSS", 8, Right: true),
        new CliTableColumn("QUALITY", 10),
    };
    var rows = new List<IReadOnlyList<string>>();

    while (!shutdownCts.IsCancellationRequested)
    {
        try
        {
            using var sampleCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
            sampleCts.CancelAfter(TimeSpan.FromSeconds(options.Test.Parameters.DurationS + options.Test.Parameters.WarmupS + 15));
            var result = await RunMeasurementAsync(options.Test, term, showProgress: false, sampleCts.Token);
            rows.Add(WatchRow(term, result));
            if (rows.Count > 12) rows.RemoveAt(0);
            Console.Out.WriteLine(term.Rich ? CliTable.Render(term, columns, rows) : string.Join("  ", rows[^1]));
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"gard: watch sample failed: {ex.Message}");
        }
        try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalS), shutdownCts.Token); }
        catch (OperationCanceledException) { break; }
    }
    return 0;
}

static int RunDoctor(string[] a, CliTerminal term)
{
    if (CliOptions.HasHelp(a)) { Console.Out.WriteLine(CliHelp.Doctor()); return 0; }
    var rows = new List<IReadOnlyList<string>>
    {
        new[] { "version", GardCliInfo.VersionString },
        new[] { "os", RuntimeInformation.OSDescription.Trim() },
        new[] { "arch", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() },
        new[] { "service", DiscoveryConstants.ServiceType },
        new[] { "default port", DiscoveryConstants.DefaultPort.ToString() },
        new[] { "install path", Environment.ProcessPath ?? "unknown" },
        new[] { "dotnet", DotnetInfo() },
    };
    foreach (var (iface, ip) in LocalIPv4()) rows.Add(new[] { $"if {iface}", ip });

    if (term.Rich)
    {
        Console.Out.WriteLine(CliPixelArt.Header(term));
        Console.Out.WriteLine();
        Console.Out.WriteLine(CliTable.Render(term, [new("CHECK", 18), new("VALUE", 58)], rows));
        Console.Out.WriteLine(term.Dim("Hint: allow TCP 7737 and ephemeral UDP ports through local firewalls."));
    }
    else
    {
        Console.Out.WriteLine("Gard doctor");
        foreach (var row in rows) Console.Out.WriteLine($"{row[0],-18} {row[1]}");
        Console.Out.WriteLine("Hint: allow TCP 7737 and ephemeral UDP ports through local firewalls.");
    }
    return 0;
}

static async Task<TestResult> RunMeasurementAsync(
    TestCommandOptions options,
    CliTerminal term,
    bool showProgress,
    CancellationToken ct = default)
{
    var parameters = options.Parameters;
    using var timerRes = WindowsTimerResolution.RaiseToOneMs();

    if (showProgress) Console.Error.WriteLine($"{term.Green("[+]")} Connecting to {options.Host}:{options.Port}...");
    await using var ctl = await TcpFrameTransport.ConnectAsync(options.Host, options.Port, cancellationToken: ct);
    if (showProgress) Console.Error.WriteLine($"{term.Green("[+]")} Control channel ready.");

    var identity = ClientIdentity();
    var hs = await ClientHandshake.PerformAsync(ctl, identity, cancellationToken: ct);
    if (showProgress) Console.Error.WriteLine($"{term.Green("[+]")} Negotiated LSP/{hs.PeerVersion}. session={Short(hs.SessionId)} caps={hs.NegotiatedCaps.ToHexString()}");

    await using var router = new ControlMessageRouter(new ControlStream(ctl, ct));
    router.Start();

    var inputs = new MeasurementClientInputs
    {
        Parameters = parameters,
        PeerName = options.Host,
        PeerPlatform = hs.PeerPlatform,
        SessionId = hs.SessionId,
        NegotiatedCaps = hs.NegotiatedCaps,
    };

    if (showProgress)
        Console.Error.WriteLine($"{term.Green("[+]")} Running {parameters.Transport.ToString().ToLowerInvariant()} {parameters.Direction.ToString().ToLowerInvariant()} test...");

    var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
    return await MeasurementSession.RunClientAsync(
        ctl, router, dataPlane, options.Host, inputs, onProgress: null, cancellationToken: ct);
}

static void PrintTestJson(TestResult r)
    => Console.Out.WriteLine(JsonSerializer.Serialize(r, CliJsonContext.Default.TestResult));

static void PrintHostBanner(DeviceIdentity id, int port, CliTerminal term)
{
    if (term.Rich)
    {
        Println(CliPixelArt.Header(term));
        Println("");
    }
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

        var dataPlane = new TcpDataPlane { BindAddress = IPAddress.IPv6Any };
        var result = await MeasurementResponder.RunAsync(
            transport, router, dataPlane, hs.SessionId, tickIntervalMs: 200, cancellationToken: ct);

        PrintHostTestSummary(connId, result);
        LogEvent(connId, "ok", $"closed session={sess}");
    }
    catch (Exception ex)
    {
        LogEvent(connId, "!!", $"error: {ex.Message}");
    }
}

static void PrintHostTestSummary(string connId, ResultBody r)
{
    LogEvent(connId, "ok", $"test complete dir={r.Direction.ToString().ToLowerInvariant()} streams={r.Streams} duration={r.DurationS:F2}s");
    Println(Timestamp() + "  " + connId + $"    {CliHumanFormatter.FormatResultBodySummary(r)} quality={CliQuality.Label(CliQuality.Grade(r))}");
    if (r.Samples > 0)
    {
        Println(Timestamp() + "  " + connId +
            $"   ping avg={r.LatencyMs.Avg:F1}ms p95={r.LatencyMs.P95:F1}ms jit={r.JitterMs:F1}ms loss={r.LossPct:F1}% (n={r.Samples})");
    }
}

static IReadOnlyList<string> WatchRow(CliTerminal term, TestResult result)
{
    var down = result.ThroughputDown?.MeanBps ?? (result.Direction == TestDirection.Down ? result.MeanBps : 0);
    var up = result.ThroughputUp?.MeanBps ?? (result.Direction == TestDirection.Up ? result.MeanBps : 0);
    return [
        DateTime.Now.ToString("HH:mm:ss"),
        Mbps(down),
        Mbps(up),
        $"{result.PingP95Ms:F1} ms",
        $"{result.LossPct:F2}%",
        CliQuality.Paint(term, CliQuality.Grade(result)),
    ];
}

static string Mbps(ulong bps) => bps == 0 ? "-" : (bps / 1_000_000d).ToString("F1");
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

static DeviceIdentity ClientIdentity() => new()
{
    Name = "gard-cli",
    Platform = DeviceIdentity.CurrentPlatform,
    AppVersion = GardCliInfo.AppVersion,
};

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

static string DotnetInfo()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return "not found";
        process.WaitForExit(1000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "not found";
    }
    catch
    {
        return "not found";
    }
}
