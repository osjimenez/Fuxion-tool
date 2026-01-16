using System.IO;
using System.Text.Json;

namespace Fuxion.Tools.Core.Configuration;

public static class FuxionToolsConfigLoader
{
	public static FuxionToolsConfig LoadOrDefault(string? configPath)
	{
		if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
			return new();

		try
		{
			var json = File.ReadAllText(configPath);
			var cfg = JsonSerializer.Deserialize<FuxionToolsConfig>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true,
				ReadCommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});

			return cfg ?? new();
		}
		catch
		{
			return new();
		}
	}
}
