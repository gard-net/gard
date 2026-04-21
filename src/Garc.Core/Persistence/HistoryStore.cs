using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Garc.Core.Models;

namespace Garc.Core.Persistence;

/// <summary>
/// Persistencia local de <see cref="TestResult"/>s en un JSON único versionado.
/// Equivalente al <c>HistoryStore</c> Swift pero agnóstico de plataforma: el
/// proyecto UI decide el directorio concreto (<c>LocalApplicationData</c> en
/// Windows, <c>ApplicationSupport/Landspeed</c> en macOS, etc.).
///
/// Estrategia de escritura:
/// - Serialización JSON con claves <c>snake_case</c> y orden estable (útil
///   para diffs humanos).
/// - Atomicidad: se escribe a <c>.history.json.tmp</c> y luego se renombra
///   sobre el archivo final vía <see cref="File.Move(string, string, bool)"/>.
/// - Concurrencia: <see cref="SemaphoreSlim"/> serializa escrituras dentro
///   del proceso. Entre procesos no se garantiza nada; se asume un solo
///   proceso tocando el archivo (caso de una app de escritorio).
/// </summary>
public sealed class HistoryStore
{
    public const int SchemaVersion = 1;
    public const string FileName = "history.json";

    private readonly string _directory;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Opciones JSON usadas para serializar el snapshot. Expuestas para que los
    /// tests puedan reutilizarlas sin duplicar configuración.
    /// </summary>
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
        return opts;
    }

    /// <summary>
    /// Crea el store apuntando a <paramref name="directory"/>. Se crea si no
    /// existe. La UI es responsable de elegir el directorio apropiado para
    /// la plataforma.
    /// </summary>
    public HistoryStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _filePath = Path.Combine(_directory, FileName);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Ruta absoluta al archivo <c>history.json</c>.</summary>
    public string FilePath => _filePath;

    /// <summary>Snapshot serializable: envoltura versionada sobre la lista.</summary>
    public sealed record Snapshot
    {
        public int SchemaVersion { get; init; } = HistoryStore.SchemaVersion;
        public IReadOnlyList<TestResult> Results { get; init; } = Array.Empty<TestResult>();
    }

    /// <summary>Carga todos los resultados, ordenados de más reciente a más antiguo.</summary>
    public async Task<IReadOnlyList<TestResult>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadUnsortedAsync(cancellationToken).ConfigureAwait(false);
            return all.OrderByDescending(r => r.StartedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Añade o actualiza un resultado (dedup por <c>Id</c>) y persiste. Devuelve
    /// el historial actualizado ya ordenado.
    /// </summary>
    public async Task<IReadOnlyList<TestResult>> AppendAsync(
        TestResult result, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await ReadUnsortedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var idx = all.FindIndex(r => r.Id == result.Id);
            if (idx >= 0) all[idx] = result; else all.Add(result);
            await WriteAllAsync(all, cancellationToken).ConfigureAwait(false);
            return all.OrderByDescending(r => r.StartedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Borra un resultado por Id. No-op si no existe.</summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await ReadUnsortedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = all.RemoveAll(r => r.Id == id);
            if (removed > 0)
            {
                await WriteAllAsync(all, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Vacía el historial.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAllAsync(Array.Empty<TestResult>(), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<TestResult>> ReadUnsortedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<TestResult>();
        await using var fs = File.OpenRead(_filePath);
        if (fs.Length == 0) return Array.Empty<TestResult>();
        var snap = await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return snap?.Results ?? Array.Empty<TestResult>();
    }

    private async Task WriteAllAsync(IReadOnlyList<TestResult> results, CancellationToken cancellationToken)
    {
        var snap = new Snapshot { Results = results };
        var tmp = Path.Combine(_directory, ".history.json.tmp");
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, snap, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }
}
