using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gard.Core.Persistence;

/// <summary>
/// Persistencia de <see cref="Preferences"/> en un JSON versionado. Misma
/// estrategia que <see cref="HistoryStore"/>: escritura atómica vía tmp+rename
/// y serialización con una <see cref="SemaphoreSlim"/> intra-proceso.
///
/// El archivo corrupto, vacío o ausente se trata como "primera ejecución" y
/// devuelve <see cref="Preferences"/> con defaults (sin borrar nada que haya
/// quedado en disco, para no perder datos recuperables a mano).
/// </summary>
public sealed class PreferencesStore
{
    public const int SchemaVersion = 1;
    public const string FileName = "preferences.json";

    private readonly string _directory;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opciones JSON compartidas; expuestas para facilitar asserts en tests.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return opts;
    }

    /// <summary>Crea el store. El directorio se crea si no existe.</summary>
    public PreferencesStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _filePath = Path.Combine(_directory, FileName);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Ruta absoluta al archivo <c>preferences.json</c>.</summary>
    public string FilePath => _filePath;

    /// <summary>Envoltura versionada sobre <see cref="Preferences"/>.</summary>
    public sealed record Snapshot
    {
        public int SchemaVersion { get; init; } = PreferencesStore.SchemaVersion;
        public Preferences Preferences { get; init; } = new();
    }

    /// <summary>
    /// Lee el archivo y devuelve las preferencias saneadas. Si no existe, está
    /// vacío o no se puede parsear, devuelve defaults.
    /// </summary>
    public async Task<Preferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return new Preferences();
            await using var fs = File.OpenRead(_filePath);
            if (fs.Length == 0) return new Preferences();
            Snapshot? snap;
            try
            {
                snap = await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Archivo corrupto: degradamos a defaults sin reescribir, así el
                // usuario puede inspeccionar el original si le interesa.
                return new Preferences();
            }
            return (snap?.Preferences ?? new Preferences()).Sanitized();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sanea y persiste las preferencias. Devuelve la versión efectivamente escrita.</summary>
    public async Task<Preferences> SaveAsync(Preferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var clean = preferences.Sanitized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(clean, cancellationToken).ConfigureAwait(false);
            return clean;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Lee, aplica la transformación y persiste en una sola operación atómica
    /// respecto a otros llamantes del mismo store. El delegate recibe las
    /// preferencias actuales (ya saneadas) y devuelve la nueva versión.
    /// </summary>
    public async Task<Preferences> UpdateAsync(
        Func<Preferences, Preferences> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var next = mutate(current).Sanitized();
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restablece todas las preferencias a sus valores por defecto.</summary>
    public async Task<Preferences> ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var defaults = new Preferences();
            await WriteAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
        finally { _gate.Release(); }
    }

    private async Task<Preferences> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new Preferences();
        await using var fs = File.OpenRead(_filePath);
        if (fs.Length == 0) return new Preferences();
        try
        {
            var snap = await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return (snap?.Preferences ?? new Preferences()).Sanitized();
        }
        catch (JsonException)
        {
            return new Preferences();
        }
    }

    private async Task WriteAsync(Preferences preferences, CancellationToken cancellationToken)
    {
        var snap = new Snapshot { Preferences = preferences };
        var tmp = Path.Combine(_directory, ".preferences.json.tmp");
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, snap, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }
}
