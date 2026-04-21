using System.Text.Json.Serialization;

namespace Garc.Core.Protocol;

// [JsonStringEnumMemberName] (.NET 9+) es el único atributo que `JsonStringEnumConverter<T>`
// respeta para override de valores. `[EnumMember]` era lo que usaba DataContract y lo
// ignora el serializer de System.Text.Json → el enum se emitiría como "Macos"/"Windows"
// (PascalCase), pero el Swift espera lowercase. Este enum SÍ SALE AL WIRE LSP/1.x —
// cambiar sin cuidado rompe interop con la app Apple.

[JsonConverter(typeof(JsonStringEnumConverter<PeerPlatform>))]
public enum PeerPlatform
{
    [JsonStringEnumMemberName("ios")]     Ios,
    [JsonStringEnumMemberName("ipados")]  IpadOs,
    [JsonStringEnumMemberName("macos")]   Macos,
    [JsonStringEnumMemberName("tvos")]    Tvos,
    [JsonStringEnumMemberName("windows")] Windows,
    [JsonStringEnumMemberName("android")] Android,
    [JsonStringEnumMemberName("linux")]   Linux,
}

[JsonConverter(typeof(JsonStringEnumConverter<TestDirection>))]
public enum TestDirection
{
    [JsonStringEnumMemberName("up")]    Up,
    [JsonStringEnumMemberName("down")]  Down,
    [JsonStringEnumMemberName("bidir")] Bidir,
}

/// <summary>LSP/1.1: modo del test bidireccional.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BidirMode>))]
public enum BidirMode
{
    [JsonStringEnumMemberName("simultaneous")] Simultaneous,
    [JsonStringEnumMemberName("sequential")]   Sequential,
}
