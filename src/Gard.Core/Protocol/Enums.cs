using System.Text.Json.Serialization;

namespace Gard.Core.Protocol;

// [JsonStringEnumMemberName] (.NET 9+) es el único atributo que `JsonStringEnumConverter<T>`
// respeta para override de valores. `[EnumMember]` era lo que usaba DataContract y lo
// ignora el serializer de System.Text.Json → el enum se emitiría como "Macos"/"Windows"
// (PascalCase), pero el Swift espera lowercase. Este enum SÍ SALE AL WIRE LSP/1.x —
// cambiar sin cuidado rompe interop con la app Apple.

[JsonConverter(typeof(LspEnumConverter<PeerPlatform>))]
public enum PeerPlatform
{
    [JsonStringEnumMemberName("ios")]     Ios,
    [JsonStringEnumMemberName("ipados")]  IpadOs,
    [JsonStringEnumMemberName("macos")]   Macos,
    [JsonStringEnumMemberName("tvos")]    Tvos,
    [JsonStringEnumMemberName("windows")] Windows,
    [JsonStringEnumMemberName("android")] Android,
    [JsonStringEnumMemberName("linux")]   Linux,
    /// <summary>
    /// El peer no anunció plataforma (hosts LSP/1.0–1.1 y la app Apple no mandan
    /// <c>hello_ack.platform</c>). Sólo se usa localmente: gard nunca lo envía.
    /// </summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}

[JsonConverter(typeof(LspEnumConverter<TestDirection>))]
public enum TestDirection
{
    [JsonStringEnumMemberName("up")]    Up,
    [JsonStringEnumMemberName("down")]  Down,
    [JsonStringEnumMemberName("bidir")] Bidir,
}

/// <summary>LSP/1.1: modo del test bidireccional.</summary>
[JsonConverter(typeof(LspEnumConverter<BidirMode>))]
public enum BidirMode
{
    [JsonStringEnumMemberName("simultaneous")] Simultaneous,
    [JsonStringEnumMemberName("sequential")]   Sequential,
}

/// <summary>LSP/1.2: transport del plano de datos. El control sigue siempre en TCP.</summary>
[JsonConverter(typeof(LspEnumConverter<TestTransport>))]
public enum TestTransport
{
    [JsonStringEnumMemberName("tcp")] Tcp,
    [JsonStringEnumMemberName("udp")] Udp,
}

/// <summary>
/// Como <see cref="JsonStringEnumConverter{TEnum}"/> pero rechaza números: el
/// wire LSP usa strings y <c>"direction": 9</c> no debe decodificar a un valor
/// fuera del enum.
/// </summary>
public sealed class LspEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public LspEnumConverter() : base(namingPolicy: null, allowIntegerValues: false) { }
}
