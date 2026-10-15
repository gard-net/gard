using System.Text.Json;
using System.Text.Json.Serialization;
using Gard.Core.Discovery;
using Gard.Core.Handshake;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Cli;

// Contrato JSON público del CLI (desde 0.3.0). Claves snake_case como el wire
// LSP y un campo `schema` versionado: agregar campos es compatible; renombrar o
// quitar exige subir el número. Antes se serializaba TestResult tal cual, en
// PascalCase y sin versión.

public sealed record TestReport
{
    public string Schema { get; init; } = "gard.test/1";
    public required string GardVersion { get; init; }
    public required string LspVersion { get; init; }
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public string? Label { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required PeerReport Peer { get; init; }
    public required TestSetupReport Test { get; init; }
    public required ThroughputReport Throughput { get; init; }
    public required LatencyReport Latency { get; init; }
    public RttUnderLoadStats? RttUnderLoad { get; init; }
    public UdpStatsBody? Udp { get; init; }
    public IntervalReport? Intervals { get; init; }
    public required string Quality { get; init; }

    public static TestReport From(TestResult r, TestParameters p, string? label) => new()
    {
        GardVersion = GardCliInfo.VersionNumber,
        LspVersion = r.ProtocolVersion.ToString(),
        Id = r.Id,
        SessionId = r.SessionId,
        Label = string.IsNullOrEmpty(label) ? null : label,
        StartedAt = r.StartedAt,
        EndedAt = r.EndedAt,
        Peer = new PeerReport { Name = r.PeerName, Platform = r.PeerPlatform },
        Test = new TestSetupReport
        {
            Direction = r.Direction,
            Transport = p.Transport,
            Streams = r.Streams,
            DurationS = r.DurationS,
            WarmupS = p.WarmupS,
            PayloadBytes = p.PayloadSize,
            BidirMode = p.BidirMode,
            TargetBitrateBps = p.Transport == TestTransport.Udp ? p.TargetBitrateBps : null,
        },
        Throughput = new ThroughputReport
        {
            MeanBps = r.MeanBps,
            PeakBps = r.PeakBps,
            PerStreamBps = r.PerStreamBps,
            Up = r.ThroughputUp,
            Down = r.ThroughputDown,
        },
        Latency = new LatencyReport
        {
            MinMs = r.PingMinMs,
            AvgMs = r.PingAvgMs,
            P95Ms = r.PingP95Ms,
            MaxMs = r.PingMaxMs,
            JitterMs = r.JitterMs,
            LossPct = r.LossPct,
            Samples = r.PingSamples,
        },
        RttUnderLoad = r.RttUnderLoadMs,
        Udp = r.Udp,
        Intervals = r.Intervals,
        Quality = CliQuality.Label(CliQuality.Grade(r)),
    };
}

public sealed record PeerReport
{
    public required string Name { get; init; }
    public required PeerPlatform Platform { get; init; }
}

public sealed record TestSetupReport
{
    public required TestDirection Direction { get; init; }
    public required TestTransport Transport { get; init; }
    public required int Streams { get; init; }
    public required double DurationS { get; init; }
    public required double WarmupS { get; init; }
    public required int PayloadBytes { get; init; }
    public BidirMode? BidirMode { get; init; }
    public ulong? TargetBitrateBps { get; init; }
}

public sealed record ThroughputReport
{
    public required ulong MeanBps { get; init; }
    public required ulong PeakBps { get; init; }
    public required IReadOnlyList<ulong> PerStreamBps { get; init; }
    public ThroughputBody? Up { get; init; }
    public ThroughputBody? Down { get; init; }
}

public sealed record LatencyReport
{
    public required double MinMs { get; init; }
    public required double AvgMs { get; init; }
    public required double P95Ms { get; init; }
    public required double MaxMs { get; init; }
    public required double JitterMs { get; init; }
    public required double LossPct { get; init; }
    public required int Samples { get; init; }
}

public sealed record ScanReport
{
    public string Schema { get; init; } = "gard.scan/1";
    public required double DurationS { get; init; }
    public required IReadOnlyList<ScanPeerReport> Peers { get; init; }
}

public sealed record ScanPeerReport
{
    public required string Name { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required int LspMajor { get; init; }
    public required string Caps { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }

    public static ScanPeerReport From(DiscoveredPeer p) => new()
    {
        Name = p.DeviceName,
        Host = p.Host,
        Port = p.Port,
        Platform = p.Platform,
        LspMajor = p.ProtocolVersionMajor,
        Caps = p.Caps.ToHexString(),
        Capabilities = CliJson.CapabilityNames(p.Caps),
    };
}

public sealed record InfoReport
{
    public string Schema { get; init; } = "gard.info/1";
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string LspVersion { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required string SessionId { get; init; }
    public required string Caps { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required IReadOnlyList<string> Transports { get; init; }
    public required double ClockRttMs { get; init; }

    public static InfoReport From(string host, int port, HandshakeOutcome hs) => new()
    {
        Host = host,
        Port = port,
        LspVersion = hs.PeerVersion.ToString(),
        Platform = hs.PeerPlatform,
        SessionId = hs.SessionId,
        Caps = hs.NegotiatedCaps.ToHexString(),
        Capabilities = CliJson.CapabilityNames(hs.NegotiatedCaps),
        Transports = hs.NegotiatedCaps.HasFlag(Gard.Core.Protocol.Capabilities.UdpDataPlane) ? ["tcp", "udp"] : ["tcp"],
        ClockRttMs = hs.ClockSyncRttNs / 1_000_000.0,
    };
}

public sealed record DoctorReport
{
    public string Schema { get; init; } = "gard.doctor/1";
    public required string GardVersion { get; init; }
    public required string LspVersion { get; init; }
    public required string Os { get; init; }
    public required string Arch { get; init; }
    public required string? InstallPath { get; init; }
    public required TerminalReport Terminal { get; init; }
    public required IReadOnlyList<InterfaceReport> Interfaces { get; init; }
}

public sealed record TerminalReport
{
    public required bool Interactive { get; init; }
    public required bool Color { get; init; }
    public required bool Unicode { get; init; }
    public required int Width { get; init; }
}

public sealed record InterfaceReport
{
    public required string Name { get; init; }
    public required string Address { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TestReport))]
[JsonSerializable(typeof(ScanReport))]
[JsonSerializable(typeof(InfoReport))]
[JsonSerializable(typeof(DoctorReport))]
internal partial class CliJsonContext : JsonSerializerContext;

public static class CliJson
{
    /// <summary>Indentado en un TTY (para leer), compacto si se redirige (para jq/NDJSON).</summary>
    public static string Serialize(TestReport r, bool indented = false) =>
        Format(JsonSerializer.Serialize(r, CliJsonContext.Default.TestReport), indented);

    public static string Serialize(ScanReport r, bool indented = false) =>
        Format(JsonSerializer.Serialize(r, CliJsonContext.Default.ScanReport), indented);

    public static string Serialize(InfoReport r, bool indented = false) =>
        Format(JsonSerializer.Serialize(r, CliJsonContext.Default.InfoReport), indented);

    public static string Serialize(DoctorReport r, bool indented = false) =>
        Format(JsonSerializer.Serialize(r, CliJsonContext.Default.DoctorReport), indented);

    private static string Format(string compact, bool indented)
    {
        if (!indented) return compact;
        using var doc = JsonDocument.Parse(compact);
        using var ms = new MemoryStream();
        // Relajado: '+' de las fechas y acentos de nombres legibles; es stdout, no HTML.
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
            doc.WriteTo(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static readonly (Capabilities Flag, string Name)[] Names =
    [
        (Capabilities.ParallelStreams, "parallel_streams"),
        (Capabilities.Bidirectional, "bidirectional"),
        (Capabilities.Tls, "tls"),
        (Capabilities.Pairing, "pairing"),
        (Capabilities.ClockSync, "clock_sync"),
        (Capabilities.DataEcho, "data_echo"),
        (Capabilities.BidirSequential, "bidir_sequential"),
        (Capabilities.IntervalReporting, "interval_reporting"),
        (Capabilities.UdpDataPlane, "udp_data_plane"),
    ];

    /// <summary>Nombres de capabilities según spec §5.</summary>
    public static IReadOnlyList<string> CapabilityNames(Capabilities caps) =>
        Names.Where(n => caps.HasFlag(n.Flag)).Select(n => n.Name).ToArray();
}
