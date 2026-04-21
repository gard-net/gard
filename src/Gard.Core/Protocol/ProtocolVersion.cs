using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gard.Core.Protocol;

/// <summary>SemVer del protocolo. Mayor distinto → rechazo (wire code 2000). Menor distinto → negociar por caps.</summary>
[JsonConverter(typeof(ProtocolVersionJsonConverter))]
public readonly record struct ProtocolVersion(int Major, int Minor)
{
    public static readonly ProtocolVersion Current = new(1, 1);

    public override string ToString() => $"{Major}.{Minor}";

    public static ProtocolVersion? Parse(string s)
    {
        var parts = s.Split('.');
        if (parts.Length is not (1 or 2)) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        var minor = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], out minor)) return null;
        return new ProtocolVersion(major, minor);
    }

    public bool IsCompatibleWith(ProtocolVersion peer) => Major == peer.Major;
}

internal sealed class ProtocolVersionJsonConverter : JsonConverter<ProtocolVersion>
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
