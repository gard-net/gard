using System.Runtime.InteropServices;
using Gard.Core.Discovery;
using Gard.Core.Protocol;

namespace Gard.Cli;

/// <summary>
/// Diagnóstico local: versión, plataforma, binario, capacidades del terminal
/// (lo que el CLI detectó: Unicode, color, ancho) e interfaces de red.
/// </summary>
public static class DoctorCommand
{
    public static int Run(string[] args, CliContext ctx)
    {
        var t = ctx.Term;
        var s = t.Symbols;
        var interfaces = NetInfo.LocalIPv4();
        var report = new DoctorReport
        {
            GardVersion = GardCliInfo.VersionNumber,
            LspVersion = ProtocolVersion.Current.ToString(),
            Os = RuntimeInformation.OSDescription.Trim(),
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            InstallPath = Environment.ProcessPath,
            Terminal = new TerminalReport
            {
                Interactive = !Console.IsOutputRedirected,
                Color = t.Color,
                Unicode = t.Unicode,
                Width = t.Width,
            },
            Interfaces = interfaces.Select(i => new InterfaceReport { Name = i.Iface, Address = i.Ip }).ToArray(),
        };

        if (CliOptions.HasFlag(args, "--json"))
        {
            ctx.Out.WriteLine(CliJson.Serialize(report, indented: !Console.IsOutputRedirected));
            return GardApp.Ok;
        }

        ctx.Out.WriteLine(t.Bold("gard doctor"));
        Check(true, "version", GardCliInfo.VersionString);
        Check(true, "platform", $"{report.Os} {s.Dot} {report.Arch}");
        Check(report.InstallPath is not null, "binary", report.InstallPath ?? "unknown");
        var termCaps = new List<string>
        {
            report.Terminal.Interactive ? "interactive" : "redirected",
            t.Unicode ? "unicode" : "ascii",
            t.Color ? "color" : "no color",
            $"{t.Width} cols",
        };
        Check(true, "terminal", string.Join($" {s.Dot} ", termCaps));
        Check(interfaces.Count > 0, "network", interfaces.Count == 0
            ? "no IPv4 interface up"
            : string.Join(", ", interfaces.Select(i => $"{i.Ip} {t.Dim($"({i.Iface})")}")));
        Warn("firewall", $"gard host needs TCP {DiscoveryConstants.DefaultPort}, ephemeral TCP/UDP data ports and mDNS (UDP 5353)");
        return GardApp.Ok;

        void Check(bool ok, string label, string value) =>
            ctx.Out.WriteLine($"  {(ok ? t.Green(s.Ok) : t.Red(s.Fail))} {t.Dim(label.PadRight(10))} {value}");
        void Warn(string label, string value) =>
            ctx.Out.WriteLine($"  {t.Yellow(s.Warn)} {t.Dim(label.PadRight(10))} {value}");
    }
}
