using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Gard.Core.Protocol;

/// <summary>
/// Codec JSON de <see cref="ControlMessage"/>. Replica el comportamiento del
/// <c>ControlMessageCodec</c> Swift: serializa el body con campos en
/// <c>snake_case</c>, inyecta <c>t</c> + <c>id</c> al mismo nivel, y emite
/// JSON con claves ordenadas alfabéticamente para facilitar diffs y tests
/// de byte-igualdad con peers Apple.
/// </summary>
public static class ControlMessageCodec
{
    public static JsonSerializerOptions DefaultOptions { get; } = BuildDefaultOptions();

    private static JsonSerializerOptions BuildDefaultOptions()
    {
        return LspJsonContext.CreateOptions();
    }

    /// <summary>
    /// Serializa el mensaje al JSON UTF-8 listo para meter en un frame
    /// CONTROL_JSON.
    /// </summary>
    public static byte[] Encode(ControlMessage message, JsonSerializerOptions? options = null)
    {
        var opts = options ?? DefaultOptions;

        // 1) Serializa el body al JSON del dominio (snake_case).
        JsonObject bodyJson = message switch
        {
            HelloMessage h         => ToObject(h.Body, LspJsonContext.Default.HelloBody),
            HelloAckMessage h      => ToObject(h.Body, LspJsonContext.Default.HelloAckBody),
            PairRequestMessage m   => ToObject(m.Body, LspJsonContext.Default.PairRequestBody),
            PairAckMessage m       => ToObject(m.Body, LspJsonContext.Default.PairAckBody),
            ClockSyncMessage m     => ToObject(m.Body, LspJsonContext.Default.ClockSyncBody),
            ClockSyncAckMessage m  => ToObject(m.Body, LspJsonContext.Default.ClockSyncAckBody),
            PingMessage m          => ToObject(m.Body, LspJsonContext.Default.PingBody),
            PongMessage m          => ToObject(m.Body, LspJsonContext.Default.PongBody),
            TestStartMessage m     => ToObject(m.Body, LspJsonContext.Default.TestStartBody),
            TestStartAckMessage m  => ToObject(m.Body, LspJsonContext.Default.TestStartAckBody),
            TestTickMessage m      => ToObject(m.Body, LspJsonContext.Default.TestTickBody),
            TestEndMessage m       => ToObject(m.Body, LspJsonContext.Default.TestEndBody),
            ResultMessage m        => ToObject(m.Body, LspJsonContext.Default.ResultBody),
            GoodbyeMessage m       => ToObject(m.Body, LspJsonContext.Default.GoodbyeBody),
            ErrorMessage m         => ToObject(m.Body, LspJsonContext.Default.ErrorBody),
            UdpStatsReportMessage m => ToObject(m.Body, LspJsonContext.Default.UdpStatsReportBody),
            _ => throw LandspeedException.InternalInconsistency($"tipo de ControlMessage desconocido: {message.GetType()}"),
        };

        // 2) Inyecta t + id al mismo nivel.
        bodyJson["t"] = message.T;
        bodyJson["id"] = message.Id;

        // 3) Emite con claves ordenadas alfabéticamente (sortedKeys).
        return SerializeSorted(bodyJson, opts);
    }

    /// <summary>
    /// Decodifica un payload CONTROL_JSON. Lanza <see cref="LandspeedException"/>
    /// con Kind <c>MalformedControlJson</c> o <c>UnknownControlMessageType</c>.
    /// </summary>
    public static ControlMessage Decode(ReadOnlySpan<byte> data, JsonSerializerOptions? options = null)
    {
        var opts = options ?? DefaultOptions;

        JsonObject root;
        try
        {
            root = (JsonNode.Parse(data.ToArray()) as JsonObject)
                ?? throw LandspeedException.MalformedControlJson("raíz no es objeto");
        }
        catch (JsonException jx)
        {
            throw LandspeedException.MalformedControlJson(jx.Message, jx);
        }

        var tNode = root["t"] ?? throw LandspeedException.MalformedControlJson("campo 't' ausente");
        var idNode = root["id"] ?? throw LandspeedException.MalformedControlJson("campo 'id' ausente");
        string t;
        ulong id;
        try
        {
            t = tNode.GetValue<string>();
            id = idNode.GetValue<ulong>();
        }
        catch (Exception ex)
        {
            throw LandspeedException.MalformedControlJson($"t/id con tipo inválido: {ex.Message}", ex);
        }

        ControlMessage Make<T>(JsonTypeInfo<T> typeInfo, Func<ulong, T, ControlMessage> ctor) where T : notnull
        {
            try
            {
                var body = root.Deserialize(typeInfo)
                    ?? throw LandspeedException.MalformedControlJson($"body nulo para {t}");
                return ctor(id, body);
            }
            catch (JsonException jx)
            {
                throw LandspeedException.MalformedControlJson(jx.Message, jx);
            }
        }

        return t switch
        {
            "hello"          => Make(LspJsonContext.Default.HelloBody, (i, b) => new HelloMessage(i, b)),
            "hello_ack"      => Make(LspJsonContext.Default.HelloAckBody, (i, b) => new HelloAckMessage(i, b)),
            "pair_request"   => Make(LspJsonContext.Default.PairRequestBody, (i, b) => new PairRequestMessage(i, b)),
            "pair_ack"       => Make(LspJsonContext.Default.PairAckBody, (i, b) => new PairAckMessage(i, b)),
            "clock_sync"     => Make(LspJsonContext.Default.ClockSyncBody, (i, b) => new ClockSyncMessage(i, b)),
            "clock_sync_ack" => Make(LspJsonContext.Default.ClockSyncAckBody, (i, b) => new ClockSyncAckMessage(i, b)),
            "ping"           => Make(LspJsonContext.Default.PingBody, (i, b) => new PingMessage(i, b)),
            "pong"           => Make(LspJsonContext.Default.PongBody, (i, b) => new PongMessage(i, b)),
            "test_start"     => Make(LspJsonContext.Default.TestStartBody, (i, b) => new TestStartMessage(i, b)),
            "test_start_ack" => Make(LspJsonContext.Default.TestStartAckBody, (i, b) => new TestStartAckMessage(i, b)),
            "test_tick"      => Make(LspJsonContext.Default.TestTickBody, (i, b) => new TestTickMessage(i, b)),
            "test_end"       => new TestEndMessage(id, new TestEndBody()),
            "result"         => Make(LspJsonContext.Default.ResultBody, (i, b) => new ResultMessage(i, b)),
            "goodbye"        => Make(LspJsonContext.Default.GoodbyeBody, (i, b) => new GoodbyeMessage(i, b)),
            "error"          => Make(LspJsonContext.Default.ErrorBody, (i, b) => new ErrorMessage(i, b)),
            "udp_stats"      => Make(LspJsonContext.Default.UdpStatsReportBody, (i, b) => new UdpStatsReportMessage(i, b)),
            _ => throw LandspeedException.UnknownControlMessageType(t),
        };
    }

    // ─── Utilidades ──────────────────────────────────────────────────────────

    /// <summary>
    /// Serializa un JsonNode con las claves ordenadas alfabéticamente en cada
    /// nivel, replicando <c>JSONSerialization.WritingOptions.sortedKeys</c>.
    /// </summary>
    private static byte[] SerializeSorted(JsonNode node, JsonSerializerOptions opts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = opts.Encoder,
            Indented = false,
            SkipValidation = true,
        }))
        {
            WriteSorted(writer, node);
        }
        return stream.ToArray();
    }

    private static JsonObject ToObject<T>(T body, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToNode(body, typeInfo) as JsonObject
            ?? throw LandspeedException.InternalInconsistency("cuerpo JSON no es objeto");

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteSorted(writer, kvp.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray arr:
                writer.WriteStartArray();
                foreach (var item in arr) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValue val:
                // Re-emitir el valor respetando tipo nativo.
                val.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Conveniencia: construye un <see cref="Frame"/> <c>CONTROL_JSON</c> con el
    /// mensaje serializado.
    /// </summary>
    public static Frame ToFrame(ControlMessage message, JsonSerializerOptions? options = null)
        => new(FrameType.ControlJson, Encode(message, options));
}
