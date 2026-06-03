using System.Text.Json.Serialization;
using Gard.Core.Models;

namespace Gard.Cli;

[JsonSerializable(typeof(TestResult))]
internal partial class CliJsonContext : JsonSerializerContext;
