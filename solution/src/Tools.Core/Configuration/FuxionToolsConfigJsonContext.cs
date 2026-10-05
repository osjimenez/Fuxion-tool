using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuxion.Tools.Core.Configuration;

/// <summary>Source-generated serialization of <c>fuxion-tools.json</c>: no reflection, so it works with native AOT.</summary>
[JsonSourceGenerationOptions(
	PropertyNameCaseInsensitive = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	AllowTrailingCommas = true)]
[JsonSerializable(typeof(FuxionToolsConfig))]
public sealed partial class FuxionToolsConfigJsonContext : JsonSerializerContext;
