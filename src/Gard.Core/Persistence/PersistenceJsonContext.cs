using System.Text.Json.Serialization;
using Gard.Core.Models;
using Gard.Core.Protocol;

namespace Gard.Core.Persistence;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = [typeof(JsonStringEnumConverter<ThroughputUnit>), typeof(JsonStringEnumConverter<AppearanceMode>)])]
[JsonSerializable(typeof(PreferencesStore.Snapshot), TypeInfoPropertyName = "PreferencesSnapshot")]
[JsonSerializable(typeof(HistoryStore.Snapshot), TypeInfoPropertyName = "HistorySnapshot")]
[JsonSerializable(typeof(Preferences))]
[JsonSerializable(typeof(TestResult))]
[JsonSerializable(typeof(NetworkInfoMetadata))]
[JsonSerializable(typeof(ThroughputBody))]
[JsonSerializable(typeof(LatencyBody))]
[JsonSerializable(typeof(RttUnderLoadStats))]
[JsonSerializable(typeof(IntervalReport))]
[JsonSerializable(typeof(IntervalSample))]
[JsonSerializable(typeof(IntervalStats))]
[JsonSerializable(typeof(UdpStatsBody))]
[JsonSerializable(typeof(OwdBody))]
public sealed partial class PersistenceJsonContext : JsonSerializerContext;
