using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Measurement;
using Gard.Core.Models;
using Gard.Core.Transport;
using Gard.Core.Utils;

namespace Gard.Cli;

/// <summary>Conexión + handshake + medición, reportando fases a un <see cref="LiveProgress"/>.</summary>
public static class MeasurementRunner
{
    public static async Task<TestResult> RunAsync(
        TestCommandOptions options,
        IMeasurementProgressSink? progress,
        Func<CancellationToken, Task<string>>? pairingCode,
        CancellationToken ct)
    {
        using var timerRes = WindowsTimerResolution.RaiseToOneMs();

        progress?.SetPhase($"connecting to {options.Host}:{options.Port}");
        await using var ctl = await TcpFrameTransport.ConnectAsync(options.Host, options.Port, cancellationToken: ct)
            .ConfigureAwait(false);

        progress?.SetPhase("handshaking");
        var hs = await ClientHandshake.PerformAsync(ctl, ClientSupport.Identity(), pairingCode, cancellationToken: ct)
            .ConfigureAwait(false);

        await using var router = new ControlMessageRouter(new ControlStream(ctl, ct));
        router.Start();

        progress?.SetPhase("measuring baseline latency");
        var inputs = new MeasurementClientInputs
        {
            Parameters = options.Parameters,
            PeerName = options.Host,
            PeerPlatform = hs.PeerPlatform,
            SessionId = hs.SessionId,
            NegotiatedCaps = hs.NegotiatedCaps,
        };
        return await MeasurementSession.RunClientAsync(
            ctl, router, new TcpDataPlane { BindAddress = IPAddress.IPv6Any }, options.Host, inputs,
            onProgress: progress is null ? null : progress.OnMeasurement,
            cancellationToken: ct).ConfigureAwait(false);
    }
}

public static class ClientSupport
{
    public static DeviceIdentity Identity() => new()
    {
        Name = "gard-cli",
        Platform = DeviceIdentity.CurrentPlatform,
        AppVersion = GardCliInfo.AppVersion,
    };

    /// <summary>
    /// Código fijo si vino por <c>--pair-code</c>; si no, se pide por terminal (una
    /// sola vez, aunque watch reconecte) cuando el host lo exige y hay TTY. Sin TTY
    /// ⇒ <c>null</c> y el handshake falla con un mensaje que indica la opción.
    /// </summary>
    public static Func<CancellationToken, Task<string>>? PairingCode(string? code, CliContext ctx)
    {
        if (code is not null) return _ => Task.FromResult(code);
        if (Console.IsInputRedirected) return null;
        string? cached = null;
        return _ =>
        {
            if (cached is null)
            {
                ctx.Err.Write($"{ctx.Term.Yellow(ctx.Term.Symbols.Warn)} host requires pairing; enter the 6-digit code it shows: ");
                cached = Console.ReadLine()?.Trim() ?? "";
            }
            return Task.FromResult(cached);
        };
    }
}

public static class NetInfo
{
    public static string DefaultHostName()
    {
        var h = Dns.GetHostName();
        return h.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? h[..^6] : h;
    }

    /// <summary>IPv4 útiles por interfaz (sin loopback ni link-local), para banner y doctor.</summary>
    public static List<(string Iface, string Ip)> LocalIPv4()
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
                if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                result.Add((ni.Name, ip));
            }
        }
        return result;
    }

    /// <summary>
    /// Tiempo de connect TCP en ms, o <c>null</c> si no responde en <paramref name="timeoutMs"/>.
    /// Separa peers vivos de anuncios mDNS cacheados de hosts que ya no existen.
    /// </summary>
    public static async Task<double?> ProbeAsync(string? host, int? port, CancellationToken ct, int timeoutMs = 800)
    {
        if (host is null || port is not { } p) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(host, p, cts.Token).ConfigureAwait(false);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary><c>[::ffff:10.0.0.2]:5000</c> ⇒ <c>10.0.0.2:5000</c>; IPv6 real queda entre corchetes.</summary>
    public static string Endpoint(EndPoint? ep) => ep switch
    {
        IPEndPoint ip when ip.Address.IsIPv4MappedToIPv6 => $"{ip.Address.MapToIPv4()}:{ip.Port}",
        IPEndPoint ip => ip.ToString(),
        null => "?",
        _ => ep.ToString() ?? "?",
    };
}
