using Gard.Core.Discovery;
using Gard.Core.Protocol;
using Gard.Core.Tests.Measurement;

namespace Gard.Core.Tests.Discovery;

/// <summary>
/// E2E de discovery: advertiser + browser en el mismo proceso compartiendo
/// loopback multicast. Aislado en la colección E2E para no pisar otros tests
/// de red y con timeout generoso porque mDNS puede tardar cientos de ms en
/// converger.
/// </summary>
[Collection(nameof(E2ETestCollection))]
public class MdnsDiscoveryE2ETests
{
    [Fact(Timeout = 15_000)]
    public async Task Advertiser_IsSeenByBrowser_WithMatchingTxt()
    {
        var identity = new DeviceIdentity
        {
            Name = "TestHost-" + Guid.NewGuid().ToString("N")[..8],
            Platform = PeerPlatform.Windows,
            AppVersion = "1.0.0",
            Caps = Capabilities.DefaultV1,
        };

        await using var advertiser = new MdnsPeerAdvertiser(identity, port: 17737);
        await using var browser = new MdnsPeerBrowser();

        await advertiser.StartAsync();
        await browser.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Buscamos específicamente nuestro instance name para evitar falsos
        // positivos si hay otro peer Landspeed en la red real.
        DiscoveredPeer? found = null;
        await foreach (var e in browser.Events.ReadAllAsync(cts.Token))
        {
            if (e.Kind == PeerEvent.EventKind.Added &&
                e.Peer is { } p &&
                p.DeviceName == identity.Name)
            {
                found = p;
                break;
            }
        }

        Assert.NotNull(found);
        Assert.Equal(identity.Name, found!.DeviceName);
        Assert.Equal(identity.Platform, found.Platform);
        Assert.Equal(identity.Caps, found.Caps);
        Assert.Equal(DiscoveryConstants.ProtocolVersionMajor, found.ProtocolVersionMajor);
        Assert.Equal(17737, found.Port);
        Assert.False(string.IsNullOrEmpty(found.Host));
    }
}
