using System.Globalization;
using System.Text;
using Garc.Core.Protocol;

namespace Garc.Core.Models;

/// <summary>
/// Convierte un <see cref="TestResult"/> en texto plano legible — sin imágenes,
/// sin adjuntos — para compartir por cualquier canal (Mensajes, Mail, Notas…).
///
/// Estilo: encabezados en MAYÚSCULAS + líneas clave:valor alineadas con
/// espacios; sin Markdown (algunos canales lo renderizan mal y el spec pide
/// texto plano estricto).
///
/// Portado de `App/UI/Shared/ResultTextFormatter.swift` del repo original Swift
/// (sección "UI Parity con app Swift original" en docs/dev-setup.md). Campos
/// Swift que NO están en el <see cref="TestResult"/> C# (payloadSize, bidirMode,
/// gapS) se omiten — agregarlos requiere extender el record primero.
/// </summary>
public static class ResultTextFormatter
{
    public static string PlainText(TestResult r, CultureInfo? culture = null)
    {
        var cx = culture ?? CultureInfo.CurrentCulture;
        var sb = new StringBuilder();

        void Line(string s = "") => sb.AppendLine(s);
        void Kv(string key, string value) => sb.AppendLine(FormatKv(key, value));

        Line("LANDSPEED — TEST DE RED");
        Line(r.StartedAt.ToLocalTime().ToString("f", cx));
        Line();

        // Resumen
        Line("RESUMEN");
        Kv("Dirección", DirectionLabel(r.Direction));
        Kv("Peer",      $"{r.PeerName} · {PlatformLabel(r.PeerPlatform)}");
        Kv("Duración",  $"{r.DurationS:0.##} s");
        Kv("Streams",   r.Streams.ToString(cx));
        Line();

        // Throughput
        Line("THROUGHPUT");
        Kv("Media", FormatMbps(BpsToMbps(r.MeanBps)));
        Kv("Pico",  FormatMbps(BpsToMbps(r.PeakBps)));
        if (r.ThroughputUp is { } up)
        {
            Kv("Subida media", FormatMbps(BpsToMbps(up.MeanBps)));
            Kv("Subida pico",  FormatMbps(BpsToMbps(up.PeakBps)));
        }
        if (r.ThroughputDown is { } dn)
        {
            Kv("Descarga media", FormatMbps(BpsToMbps(dn.MeanBps)));
            Kv("Descarga pico",  FormatMbps(BpsToMbps(dn.PeakBps)));
        }
        for (int i = 0; i < r.PerStreamBps.Count; i++)
        {
            Kv($"Stream #{i + 1}", FormatMbps(BpsToMbps(r.PerStreamBps[i])));
        }
        Line();

        // Latencia (ping de control)
        Line("LATENCIA (PING DE CONTROL)");
        // Fallback del Swift para registros antiguos sin min/max/p95 (valores ≤ 0).
        var latMin = r.PingMinMs > 0 ? r.PingMinMs : r.PingAvgMs * 0.6;
        var latMax = r.PingMaxMs > 0 ? r.PingMaxMs : r.PingAvgMs * 2.4;
        var latP95 = r.PingP95Ms > 0 ? r.PingP95Ms : r.PingAvgMs * 1.8;
        Kv("Mín",     FormatMs(latMin));
        Kv("Media",   FormatMs(r.PingAvgMs));
        Kv("Máx",     FormatMs(latMax));
        Kv("p95",     FormatMs(latP95));
        Kv("Jitter",  FormatMs(r.JitterMs));
        Kv("Pérdida", string.Format(cx, "{0:0.0} %", r.LossPct));
        if (r.PingSamples > 0)
        {
            Kv("Muestras", r.PingSamples.ToString(cx));
        }
        Line();

        // RTT bajo carga (LSP/1.1)
        if (r.RttUnderLoadMs is { } rtt)
        {
            Line("RTT BAJO CARGA");
            Kv("Muestras",  rtt.Samples.ToString(cx));
            Kv("Mediana",   FormatMs(rtt.MedianMs));
            Kv("p95",       FormatMs(rtt.P95Ms));
            Kv("Mín",       FormatMs(rtt.MinMs));
            Kv("Máx",       FormatMs(rtt.MaxMs));
            Kv("Stdev",     FormatMs(rtt.StdevMs));
            Kv("Baseline",  FormatMs(rtt.BaselineMedianMs));
            Kv("Picos >3×", rtt.SpikesCount.ToString(cx));
            Line();
        }

        // Intervalos (LSP/1.1)
        if (r.Intervals is { } rep)
        {
            Line($"INTERVALOS ({rep.WindowMs} ms)");
            Kv("Ventanas", rep.Samples.Count.ToString(cx));
            Kv("Mediana",  FormatMbps(BpsToMbps(rep.Stats.MedianBps)));
            Kv("p95",      FormatMbps(BpsToMbps(rep.Stats.P95Bps)));
            Kv("Mín",      FormatMbps(BpsToMbps(rep.Stats.MinBps)));
            Kv("Máx",      FormatMbps(BpsToMbps(rep.Stats.MaxBps)));
            Kv("Stdev",    FormatMbps(BpsToMbps(rep.Stats.StdevBps)));
            Line();
        }

        // Red (LSP/1.1 client-side)
        if (r.NetworkInfo is { } n)
        {
            Line("RED");
            if (n.NetworkType is { } type)
            {
                Kv("Tipo", type);
            }
            if (n.InterfaceName is { } iface)
            {
                Kv("Interfaz", iface);
            }
            if (n.LocalIp is { } lip)
            {
                var port = n.LocalPort is { } lp ? $":{lp}" : "";
                Kv("IP local", $"{lip}{port}");
            }
            if (n.PeerIp is { } pip)
            {
                var port = n.PeerPort is { } pp ? $":{pp}" : "";
                Kv("IP peer", $"{pip}{port}");
            }
            Kv("Zona horaria", $"{n.TimezoneIdentifier} ({FormatOffset(n.UtcOffsetMinutes)})");
            Line();
        }

        // Sesión
        Line("SESIÓN");
        Kv("ID",        string.IsNullOrEmpty(r.SessionId) ? r.Id[..Math.Min(8, r.Id.Length)] : r.SessionId);
        Kv("Protocolo", r.ProtocolVersion.ToString());

        Line();
        Line("Medido con Landspeed sobre red local.");

        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string FormatKv(string key, string value)
    {
        // Línea plana key: value, padding cosmético (mínimo 12 chars en la clave).
        var padded = key.PadRight(Math.Max(key.Length, 12));
        return $"{padded} : {value}";
    }

    private static double BpsToMbps(ulong bps) => bps / 1_000_000.0;

    private static string FormatMbps(double v)
    {
        // Formato idéntico al Swift: saltos 1000/100/10.
        var inv = CultureInfo.InvariantCulture;
        if (v >= 1000) return string.Format(inv, "{0:0.00} Gb/s", v / 1000.0);
        if (v >= 100)  return string.Format(inv, "{0:0} Mb/s",    v);
        if (v >= 10)   return string.Format(inv, "{0:0.0} Mb/s",  v);
        return string.Format(inv, "{0:0.00} Mb/s", v);
    }

    private static string FormatMs(double v)
        => string.Format(CultureInfo.InvariantCulture, "{0:0.00} ms", v);

    private static string FormatOffset(int minutes)
    {
        var sign = minutes >= 0 ? "+" : "-";
        var m = Math.Abs(minutes);
        return string.Format(CultureInfo.InvariantCulture, "UTC{0}{1:00}:{2:00}", sign, m / 60, m % 60);
    }

    private static string DirectionLabel(TestDirection d) => d switch
    {
        TestDirection.Down  => "Descarga",
        TestDirection.Up    => "Subida",
        TestDirection.Bidir => "Bidireccional",
        _ => d.ToString(),
    };

    private static string PlatformLabel(PeerPlatform p) => p switch
    {
        PeerPlatform.Macos   => "macOS",
        PeerPlatform.Ios     => "iOS",
        PeerPlatform.IpadOs  => "iPadOS",
        PeerPlatform.Tvos    => "tvOS",
        PeerPlatform.Windows => "Windows",
        PeerPlatform.Android => "Android",
        PeerPlatform.Linux   => "Linux",
        _ => p.ToString(),
    };
}
