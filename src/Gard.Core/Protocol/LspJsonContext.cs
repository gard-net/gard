using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gard.Core.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HelloBody))]
[JsonSerializable(typeof(HelloAckBody))]
[JsonSerializable(typeof(PairRequestBody))]
[JsonSerializable(typeof(PairAckBody))]
[JsonSerializable(typeof(ClockSyncBody))]
[JsonSerializable(typeof(ClockSyncAckBody))]
[JsonSerializable(typeof(PingBody))]
[JsonSerializable(typeof(PongBody))]
[JsonSerializable(typeof(TestStartBody))]
[JsonSerializable(typeof(TestStartAckBody))]
[JsonSerializable(typeof(TestTickBody))]
[JsonSerializable(typeof(TestEndBody))]
[JsonSerializable(typeof(ResultBody))]
[JsonSerializable(typeof(GoodbyeBody))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(UdpStatsReportBody))]
[JsonSerializable(typeof(ThroughputBody))]
[JsonSerializable(typeof(LatencyBody))]
[JsonSerializable(typeof(RttUnderLoadStats))]
[JsonSerializable(typeof(IntervalReport))]
[JsonSerializable(typeof(IntervalSample))]
[JsonSerializable(typeof(IntervalStats))]
[JsonSerializable(typeof(UdpStatsBody))]
[JsonSerializable(typeof(OwdBody))]
[JsonSerializable(typeof(UdpStatsPerStream))]
public sealed partial class LspJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
            TypeInfoResolver = Default,
        };
        return opts;
    }
}
