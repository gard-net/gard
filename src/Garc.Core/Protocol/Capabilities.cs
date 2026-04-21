using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Garc.Core.Protocol;

/// <summary>
/// Bitmask uint32 de capacidades LSP/1 (spec §5). Transmitido como hexadecimal
/// minúsculas en el TXT record y en los mensajes hello / hello_ack.
/// </summary>
[Flags]
[JsonConverter(typeof(CapabilitiesJsonConverter))]
public enum Capabilities : uint
{
    None = 0,
    ParallelStreams    = 0x0001,
    Bidirectional      = 0x0002,
    Tls                = 0x0004,
    Pairing            = 0x0008,
    ClockSync          = 0x0010,
    DataEcho           = 0x0020,
    BidirSequential    = 0x0040,
    IntervalReporting  = 0x0080,

    /// <summary>
    /// Capacidades típicas anunciadas por un peer LSP/1.x (incluye bits de 1.1).
    /// Idéntico a <c>Capabilities.defaultV1</c> de la app Apple. TLS queda fuera:
    /// la app de referencia aún no lo implementa (spec §7).
    /// </summary>
    DefaultV1 =
        ParallelStreams | Bidirectional | Pairing | ClockSync |
        DataEcho | BidirSequential | IntervalReporting,
}

public static class CapabilitiesExtensions
{
    /// <summary>Representación hex minúscula sin prefijo, mínimo 1 dígito.</summary>
    public static string ToHexString(this Capabilities c)
        => ((uint)c).ToString("x", CultureInfo.InvariantCulture);

    public static Capabilities? ParseHex(string hex)
    {
        var s = hex;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0) return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        return (Capabilities)v;
    }

    public static Capabilities Intersection(this Capabilities a, Capabilities b) => a & b;
}

internal sealed class CapabilitiesJsonConverter : JsonConverter<Capabilities>
{
    public override Capabilities Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Accept uint or hex string (wire uses raw uint in JSON per Swift encoder);
        // Swift encodes via Codable OptionSet → bare uint. So primary is number.
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (Capabilities)reader.GetUInt32();
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "0";
            return CapabilitiesExtensions.ParseHex(s)
                ?? throw new JsonException($"Capabilities hex inválido: {s}");
        }
        throw new JsonException("Capabilities debe ser number o string hex");
    }

    public override void Write(Utf8JsonWriter writer, Capabilities value, JsonSerializerOptions options)
        => writer.WriteNumberValue((uint)value);
}
