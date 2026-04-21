using System.Text.Json;
using Garc.Core.Models;
using Garc.Core.Persistence;

namespace Garc.Core.Tests.Persistence;

public class PreferencesStoreTests : IDisposable
{
    private readonly string _dir;

    public PreferencesStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "landspeed-prefs-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Load_NoFile_ReturnsDefaults()
    {
        var store = new PreferencesStore(_dir);
        var prefs = await store.LoadAsync();

        Assert.Equal(ThroughputUnit.Mbps, prefs.ThroughputUnit);
        Assert.Equal(4, prefs.DefaultStreams);
        Assert.Equal(10.0, prefs.DefaultDurationS);
        Assert.False(prefs.HasSeenOnboarding);
        Assert.Equal(AppearanceMode.System, prefs.Appearance);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrip()
    {
        var store = new PreferencesStore(_dir);
        var toSave = new Preferences
        {
            ThroughputUnit = ThroughputUnit.MBps,
            DefaultStreams = 8,
            DefaultDurationS = 30.0,
            HasSeenOnboarding = true,
            Appearance = AppearanceMode.Dark,
        };
        await store.SaveAsync(toSave);

        var other = new PreferencesStore(_dir);
        var loaded = await other.LoadAsync();

        Assert.Equal(ThroughputUnit.MBps, loaded.ThroughputUnit);
        Assert.Equal(8, loaded.DefaultStreams);
        Assert.Equal(30.0, loaded.DefaultDurationS);
        Assert.True(loaded.HasSeenOnboarding);
        Assert.Equal(AppearanceMode.Dark, loaded.Appearance);
    }

    [Fact]
    public async Task Save_ClampsStreamsAndDuration()
    {
        var store = new PreferencesStore(_dir);
        var saved = await store.SaveAsync(new Preferences
        {
            DefaultStreams = 100,
            DefaultDurationS = 9999.0,
        });
        Assert.Equal(16, saved.DefaultStreams);
        Assert.Equal(60.0, saved.DefaultDurationS);

        var saved2 = await store.SaveAsync(new Preferences
        {
            DefaultStreams = 0,
            DefaultDurationS = 0.1,
        });
        Assert.Equal(1, saved2.DefaultStreams);
        Assert.Equal(1.0, saved2.DefaultDurationS);
    }

    [Fact]
    public async Task Save_NonFiniteDuration_FallsBackToDefault()
    {
        var store = new PreferencesStore(_dir);
        var saved = await store.SaveAsync(new Preferences { DefaultDurationS = double.NaN });
        Assert.Equal(10.0, saved.DefaultDurationS);
    }

    [Fact]
    public async Task Update_AppliesMutationAtomically()
    {
        var store = new PreferencesStore(_dir);
        await store.SaveAsync(new Preferences { DefaultStreams = 4 });

        var updated = await store.UpdateAsync(p => p with { DefaultStreams = p.DefaultStreams + 1, HasSeenOnboarding = true });
        Assert.Equal(5, updated.DefaultStreams);
        Assert.True(updated.HasSeenOnboarding);

        var loaded = await store.LoadAsync();
        Assert.Equal(5, loaded.DefaultStreams);
        Assert.True(loaded.HasSeenOnboarding);
    }

    [Fact]
    public async Task Reset_RestoresDefaults()
    {
        var store = new PreferencesStore(_dir);
        await store.SaveAsync(new Preferences
        {
            DefaultStreams = 10,
            Appearance = AppearanceMode.Dark,
            HasSeenOnboarding = true,
        });
        var reset = await store.ResetAsync();

        Assert.Equal(4, reset.DefaultStreams);
        Assert.Equal(AppearanceMode.System, reset.Appearance);
        Assert.False(reset.HasSeenOnboarding);

        var loaded = await store.LoadAsync();
        Assert.Equal(4, loaded.DefaultStreams);
        Assert.Equal(AppearanceMode.System, loaded.Appearance);
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaults()
    {
        var store = new PreferencesStore(_dir);
        await File.WriteAllTextAsync(store.FilePath, "{not json at all");

        var prefs = await store.LoadAsync();
        Assert.Equal(4, prefs.DefaultStreams);
        Assert.Equal(ThroughputUnit.Mbps, prefs.ThroughputUnit);

        // El archivo corrupto NO se reescribe en Load: se preserva para diagnóstico.
        var raw = await File.ReadAllTextAsync(store.FilePath);
        Assert.Equal("{not json at all", raw);
    }

    [Fact]
    public async Task Load_EmptyFile_ReturnsDefaults()
    {
        var store = new PreferencesStore(_dir);
        await File.WriteAllTextAsync(store.FilePath, string.Empty);

        var prefs = await store.LoadAsync();
        Assert.Equal(4, prefs.DefaultStreams);
    }

    [Fact]
    public async Task Load_OutOfRangeValues_AreSanitized()
    {
        var store = new PreferencesStore(_dir);
        // Simular un JSON editado a mano con valores fuera de rango.
        var rogue = """
        {
          "schema_version": 1,
          "preferences": {
            "throughput_unit": "mbps",
            "default_streams": 99,
            "default_duration_s": 999.0,
            "has_seen_onboarding": false,
            "appearance": "system"
          }
        }
        """;
        await File.WriteAllTextAsync(store.FilePath, rogue);

        var prefs = await store.LoadAsync();
        Assert.Equal(16, prefs.DefaultStreams);
        Assert.Equal(60.0, prefs.DefaultDurationS);
    }

    [Fact]
    public async Task Save_ProducesSnakeCaseSchema()
    {
        var store = new PreferencesStore(_dir);
        await store.SaveAsync(new Preferences
        {
            ThroughputUnit = ThroughputUnit.Mibps,
            Appearance = AppearanceMode.Light,
            DefaultStreams = 6,
            DefaultDurationS = 20.0,
            HasSeenOnboarding = true,
        });

        var raw = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("\"schema_version\"", raw);
        Assert.Contains("\"preferences\"", raw);
        Assert.Contains("\"throughput_unit\"", raw);
        Assert.Contains("\"default_streams\"", raw);
        Assert.Contains("\"default_duration_s\"", raw);
        Assert.Contains("\"has_seen_onboarding\"", raw);
        Assert.Contains("\"appearance\"", raw);
        // Enums como string snake_case.
        Assert.Contains("\"mibps\"", raw);
        Assert.Contains("\"light\"", raw);

        // Y vuelve a parsear sin problemas.
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public async Task Save_IsAtomic_NoLeftoverTmp()
    {
        var store = new PreferencesStore(_dir);
        await store.SaveAsync(new Preferences { DefaultStreams = 2 });

        var leftovers = Directory.GetFiles(_dir, ".preferences.json.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task ConcurrentUpdates_AllApplied()
    {
        var store = new PreferencesStore(_dir);
        await store.SaveAsync(new Preferences { DefaultStreams = 1 });

        // 20 updates en paralelo sumando 0 al campo: validamos que ninguno pise
        // al otro y que el archivo siga siendo parseable. También disparamos
        // toggles del flag booleano para ejercitar el path de mutación.
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            store.UpdateAsync(p => p with { HasSeenOnboarding = !p.HasSeenOnboarding })).ToArray();
        await Task.WhenAll(tasks);

        var loaded = await store.LoadAsync();
        // Después de 20 toggles partiendo de false, termina en false.
        Assert.False(loaded.HasSeenOnboarding);
        Assert.Equal(1, loaded.DefaultStreams);
    }
}
