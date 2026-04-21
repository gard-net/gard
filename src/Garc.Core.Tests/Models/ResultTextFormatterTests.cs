using System.Globalization;
using Garc.Core.Models;
using Garc.Core.Protocol;

namespace Garc.Core.Tests.Models;

public class ResultTextFormatterTests
{
    private static TestResult MinimalResult(Action<TestResultBuilder>? configure = null)
    {
        var b = new TestResultBuilder();
        configure?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public void PlainText_IncluyeHeaderFooterYSeccionesBase()
    {
        var r = MinimalResult();
        var txt = ResultTextFormatter.PlainText(r, CultureInfo.InvariantCulture);

        Assert.StartsWith("LANDSPEED — TEST DE RED", txt);
        Assert.Contains("RESUMEN", txt);
        Assert.Contains("THROUGHPUT", txt);
        Assert.Contains("LATENCIA (PING DE CONTROL)", txt);
        Assert.Contains("SESIÓN", txt);
        Assert.EndsWith("Medido con Landspeed sobre red local.", txt);
    }

    [Fact]
    public void PlainText_TraducaDireccionAEspanol()
    {
        Assert.Contains("Descarga",      ResultTextFormatter.PlainText(MinimalResult(b => b.Direction = TestDirection.Down), CultureInfo.InvariantCulture));
        Assert.Contains("Subida",        ResultTextFormatter.PlainText(MinimalResult(b => b.Direction = TestDirection.Up),   CultureInfo.InvariantCulture));
        Assert.Contains("Bidireccional", ResultTextFormatter.PlainText(MinimalResult(b => b.Direction = TestDirection.Bidir),CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PlainText_FormateaMbpsSegunMagnitud()
    {
        // <10 → "X.XX Mb/s", 10-99 → "XX.X Mb/s", 100-999 → "XXX Mb/s", ≥1000 → "X.XX Gb/s".
        var r1 = MinimalResult(b => b.MeanBps = 5_000_000);       // 5 Mbps
        var r2 = MinimalResult(b => b.MeanBps = 55_000_000);      // 55 Mbps
        var r3 = MinimalResult(b => b.MeanBps = 250_000_000);     // 250 Mbps
        var r4 = MinimalResult(b => b.MeanBps = 2_500_000_000);   // 2500 Mbps → 2.50 Gb/s

        Assert.Contains("5.00 Mb/s",   ResultTextFormatter.PlainText(r1, CultureInfo.InvariantCulture));
        Assert.Contains("55.0 Mb/s",   ResultTextFormatter.PlainText(r2, CultureInfo.InvariantCulture));
        Assert.Contains("250 Mb/s",    ResultTextFormatter.PlainText(r3, CultureInfo.InvariantCulture));
        Assert.Contains("2.50 Gb/s",   ResultTextFormatter.PlainText(r4, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PlainText_OmiteSeccionesOpcionalesSiNull()
    {
        var r = MinimalResult();
        var txt = ResultTextFormatter.PlainText(r, CultureInfo.InvariantCulture);

        Assert.DoesNotContain("RTT BAJO CARGA", txt);
        Assert.DoesNotContain("INTERVALOS",     txt);
        // La palabra "RED" también aparece en el header ("TEST DE RED"), así
        // que chequeamos ausencia de un label que solo aparece en esa sección.
        Assert.DoesNotContain("Zona horaria", txt);
    }

    [Fact]
    public void PlainText_IncluyeRttUnderLoadSiPresente()
    {
        var r = MinimalResult(b => b.Rtt = new RttUnderLoadStats
        {
            Samples = 100,
            MedianMs = 1.23,
            P95Ms = 4.56,
            MinMs = 0.5,
            MaxMs = 9.0,
            StdevMs = 1.1,
            BaselineMedianMs = 0.9,
            SpikesCount = 3,
        });

        var txt = ResultTextFormatter.PlainText(r, CultureInfo.InvariantCulture);

        Assert.Contains("RTT BAJO CARGA", txt);
        Assert.Contains("Picos >3×", txt);
        Assert.Contains("1.23 ms", txt);
    }

    [Fact]
    public void PlainText_CabecerasKvTienenPaddingMinimo12()
    {
        var r = MinimalResult();
        var txt = ResultTextFormatter.PlainText(r, CultureInfo.InvariantCulture);

        // "Media" es 5 chars → padded a 12. Luego " : ".
        Assert.Contains("Media        : ", txt);
        // "Pico" es 4 → también 12.
        Assert.Contains("Pico         : ", txt);
    }

    [Fact]
    public void PlainText_PerStreamEnumeradoBase1()
    {
        var r = MinimalResult(b => b.PerStreamBps = new ulong[] { 10_000_000, 20_000_000, 30_000_000 });
        var txt = ResultTextFormatter.PlainText(r, CultureInfo.InvariantCulture);

        Assert.Contains("Stream #1",  txt);
        Assert.Contains("Stream #2",  txt);
        Assert.Contains("Stream #3",  txt);
        Assert.DoesNotContain("Stream #0", txt);
    }

    private sealed class TestResultBuilder
    {
        public TestDirection Direction { get; set; } = TestDirection.Down;
        public ulong MeanBps { get; set; } = 95_000_000;
        public ulong PeakBps { get; set; } = 110_000_000;
        public IReadOnlyList<ulong> PerStreamBps { get; set; } = new ulong[] { 50_000_000, 45_000_000 };
        public RttUnderLoadStats? Rtt { get; set; }

        public TestResult Build() => new()
        {
            SessionId = "sess-abc",
            PeerName = "MBP-test",
            PeerPlatform = PeerPlatform.Macos,
            StartedAt = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero),
            EndedAt   = new DateTimeOffset(2026, 4, 19, 12, 0, 10, TimeSpan.Zero),
            Direction = Direction,
            Streams = 4,
            DurationS = 10.0,
            MeanBps = MeanBps,
            PeakBps = PeakBps,
            PerStreamBps = PerStreamBps,
            PingMinMs = 0.8,
            PingAvgMs = 1.2,
            PingMaxMs = 2.5,
            PingP95Ms = 2.0,
            JitterMs = 0.3,
            LossPct = 0.0,
            PingSamples = 50,
            RttUnderLoadMs = Rtt,
        };
    }
}
