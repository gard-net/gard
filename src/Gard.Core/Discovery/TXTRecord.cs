using System.Text;
using Gard.Core.Protocol;

namespace Gard.Core.Discovery;

/// <summary>
/// TXT record de un peer LSP/1 (spec §1). Usa un <see cref="IReadOnlyDictionary{String,String}"/>
/// como representación intermedia neutra de plataforma: el código de Windows
/// lo traduce a <c>DnssdServiceInstance.TextAttributes</c>, y el código de
/// Android lo traduce a <c>NsdServiceInfo.Attributes</c>.
/// </summary>
public sealed record LandspeedTxtRecord
{
    public required int ProtocolVersionMajor { get; init; }
    public required string Name { get; init; }
    public required PeerPlatform Platform { get; init; }
    public required Capabilities Caps { get; init; }

    public IReadOnlyDictionary<string, string> ToAttributes()
    {
        return new Dictionary<string, string>
        {
            ["v"]        = ProtocolVersionMajor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["name"]     = TruncateUtf8(Name, 63),
            ["platform"] = PlatformToWire(Platform),
            ["caps"]     = Caps.ToHexString(),
        };
    }

    public static LandspeedTxtRecord? Parse(IReadOnlyDictionary<string, string> attrs)
    {
        if (!attrs.TryGetValue("v", out var vStr) ||
            !int.TryParse(vStr, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var v))
            return null;
        var name = attrs.TryGetValue("name", out var n) ? n : string.Empty;
        var platform = attrs.TryGetValue("platform", out var p) ? PlatformFromWire(p) : PeerPlatform.Linux;
        var capsStr = attrs.TryGetValue("caps", out var c) ? c : "0";
        var caps = CapabilitiesExtensions.ParseHex(capsStr) ?? Capabilities.None;

        return new LandspeedTxtRecord
        {
            ProtocolVersionMajor = v,
            Name = name,
            Platform = platform,
            Caps = caps,
        };
    }

    private static string TruncateUtf8(string s, int maxBytes)
    {
        while (Encoding.UTF8.GetByteCount(s) > maxBytes && s.Length > 0)
        {
            s = s[..^1];
        }
        return s;
    }

    private static string PlatformToWire(PeerPlatform p) => p switch
    {
        PeerPlatform.Ios     => "ios",
        PeerPlatform.IpadOs  => "ipados",
        PeerPlatform.Macos   => "macos",
        PeerPlatform.Tvos    => "tvos",
        PeerPlatform.Windows => "windows",
        PeerPlatform.Android => "android",
        PeerPlatform.Linux   => "linux",
        _ => "linux",
    };

    private static PeerPlatform PlatformFromWire(string s) => s switch
    {
        "ios"     => PeerPlatform.Ios,
        "ipados"  => PeerPlatform.IpadOs,
        "macos"   => PeerPlatform.Macos,
        "tvos"    => PeerPlatform.Tvos,
        "windows" => PeerPlatform.Windows,
        "android" => PeerPlatform.Android,
        _         => PeerPlatform.Linux,
    };
}
