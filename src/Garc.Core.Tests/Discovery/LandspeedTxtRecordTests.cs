using Garc.Core.Discovery;
using Garc.Core.Protocol;

namespace Garc.Core.Tests.Discovery;

public class LandspeedTxtRecordTests
{
    [Fact]
    public void ToAttributes_EmitsAllFourKeys()
    {
        var rec = new LandspeedTxtRecord
        {
            ProtocolVersionMajor = 1,
            Name = "MyMac",
            Platform = PeerPlatform.Macos,
            Caps = Capabilities.DefaultV1,
        };
        var attrs = rec.ToAttributes();

        Assert.Equal("1", attrs["v"]);
        Assert.Equal("MyMac", attrs["name"]);
        Assert.Equal("macos", attrs["platform"]);
        // DefaultV1 = 0xFB (spec).
        Assert.Equal("fb", attrs["caps"]);
    }

    [Fact]
    public void Parse_RoundTripsAllFields()
    {
        var original = new LandspeedTxtRecord
        {
            ProtocolVersionMajor = 1,
            Name = "Windows-PC",
            Platform = PeerPlatform.Windows,
            Caps = Capabilities.ParallelStreams | Capabilities.IntervalReporting,
        };
        var parsed = LandspeedTxtRecord.Parse(original.ToAttributes());

        Assert.NotNull(parsed);
        Assert.Equal(original.ProtocolVersionMajor, parsed!.ProtocolVersionMajor);
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Platform, parsed.Platform);
        Assert.Equal(original.Caps, parsed.Caps);
    }

    [Fact]
    public void Parse_MissingVersion_ReturnsNull()
    {
        var attrs = new Dictionary<string, string>
        {
            ["name"] = "x",
            ["platform"] = "macos",
            ["caps"] = "fb",
        };
        Assert.Null(LandspeedTxtRecord.Parse(attrs));
    }

    [Fact]
    public void Parse_UnparseableVersion_ReturnsNull()
    {
        var attrs = new Dictionary<string, string>
        {
            ["v"] = "not-a-number",
            ["name"] = "x",
        };
        Assert.Null(LandspeedTxtRecord.Parse(attrs));
    }

    [Fact]
    public void Parse_UnknownPlatform_FallsBackToLinux()
    {
        var attrs = new Dictionary<string, string>
        {
            ["v"] = "1",
            ["name"] = "x",
            ["platform"] = "haiku",
            ["caps"] = "00",
        };
        var parsed = LandspeedTxtRecord.Parse(attrs);
        Assert.NotNull(parsed);
        Assert.Equal(PeerPlatform.Linux, parsed!.Platform);
    }

    [Fact]
    public void Parse_MissingNameOrCaps_UsesDefaults()
    {
        var attrs = new Dictionary<string, string> { ["v"] = "1" };
        var parsed = LandspeedTxtRecord.Parse(attrs);
        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.Name);
        Assert.Equal(Capabilities.None, parsed.Caps);
    }

    [Fact]
    public void ToAttributes_TruncatesNameTo63Utf8Bytes()
    {
        // 65 ASCII chars → 65 bytes, debe truncarse a 63.
        var longName = new string('a', 65);
        var rec = new LandspeedTxtRecord
        {
            ProtocolVersionMajor = 1,
            Name = longName,
            Platform = PeerPlatform.Windows,
            Caps = Capabilities.None,
        };
        var attrs = rec.ToAttributes();
        Assert.Equal(63, System.Text.Encoding.UTF8.GetByteCount(attrs["name"]));
    }

    [Fact]
    public void ToAttributes_TruncatesMultiByteUtf8WithoutBreakingChars()
    {
        // Cada 'ñ' son 2 bytes UTF-8. 32 'ñ' = 64 bytes → debe quedar en 62 (31 'ñ')
        // en lugar de dejar un byte colgante.
        var name = new string('ñ', 32);
        var rec = new LandspeedTxtRecord
        {
            ProtocolVersionMajor = 1,
            Name = name,
            Platform = PeerPlatform.Windows,
            Caps = Capabilities.None,
        };
        var attrs = rec.ToAttributes();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(attrs["name"]);
        Assert.True(bytes <= 63);
        // No debe haber chars rotos: el decode/encode round-trips.
        Assert.Equal(attrs["name"], System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(attrs["name"])));
    }

    [Fact]
    public void Parse_AcceptsAllKnownPlatforms()
    {
        var pairs = new (string wire, PeerPlatform expected)[]
        {
            ("ios", PeerPlatform.Ios),
            ("ipados", PeerPlatform.IpadOs),
            ("macos", PeerPlatform.Macos),
            ("tvos", PeerPlatform.Tvos),
            ("windows", PeerPlatform.Windows),
            ("android", PeerPlatform.Android),
            ("linux", PeerPlatform.Linux),
        };
        foreach (var (wire, expected) in pairs)
        {
            var attrs = new Dictionary<string, string>
            {
                ["v"] = "1", ["name"] = "x", ["platform"] = wire, ["caps"] = "0",
            };
            var parsed = LandspeedTxtRecord.Parse(attrs);
            Assert.Equal(expected, parsed!.Platform);
        }
    }
}
