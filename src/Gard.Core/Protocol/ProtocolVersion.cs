using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gard.Core.Protocol;

/// <summary>SemVer del protocolo. Mayor distinto → rechazo (wire code 2000). Menor distinto → negociar por caps.</summary>
[JsonConverter(typeof(ProtocolVersionJsonConverter))]
public readonly record struct ProtocolVersion(int Major, int Minor)
{
    public static readonly ProtocolVersion Current = new(1, 2);

    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>
    /// Acepta <c>"1"</c>, <c>"1.2"</c> y SemVer completo (<c>"1.2.0"</c>,
    /// <c>"1.2.3-beta"</c>): el patch no afecta la compatibilidad (§9) y se ignora.
    /// </summary>
    public static ProtocolVersion? Parse(string s)
    {
        var parts = s.Split('.', 3);
        if (!TryParseNumber(parts[0], out var major)) return null;
        var minor = 0;
        if (parts.Length >= 2 && !TryParseNumber(parts[1], out minor)) return null;
        if (parts.Length == 3 && (parts[2].Length == 0 || !char.IsAsciiDigit(parts[2][0]))) return null;
        return new ProtocolVersion(major, minor);
    }

    private static bool TryParseNumber(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    public bool IsCompatibleWith(ProtocolVersion peer) => Major == peer.Major;
}

public sealed class ProtocolVersionJsonConverter : JsonConverter<ProtocolVersion>
{
    public override ProtocolVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString()
            ?? throw new JsonException("ProtocolVersion must be a string");
        return ProtocolVersion.Parse(s)
            ?? throw new JsonException($"Versión de protocolo inválida: {s}");
    }

    public override void Write(Utf8JsonWriter writer, ProtocolVersion value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
