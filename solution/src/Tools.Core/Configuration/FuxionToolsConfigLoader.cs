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
			var cfg = JsonSerializer.Deserialize(json, FuxionToolsConfigJsonContext.Default.FuxionToolsConfig);

			return cfg ?? new();
		}
		catch
		{
			return new();
		}
	}
}
