using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fuxion;
using Fuxion.Collections.Generic;
using Fuxion.Net.Http;
using Microsoft.Extensions.Configuration;

namespace NugetPackageManager;

internal class NugetClient
{
	public NugetClient()
	{
		var apiKey = new ConfigurationBuilder()
			.AddUserSecrets<Program>() // lee user-secrets de este proyecto
			.Build()["NuGet-ApiKey"];
		_client = new(new HttpClientHandler
		{
			AutomaticDecompression =
				DecompressionMethods.GZip |
				DecompressionMethods.Deflate |
				DecompressionMethods.Brotli
		})
		{
			BaseAddress = new("https://api.nuget.org"),
			DefaultRequestHeaders = { { "X-NuGet-ApiKey", apiKey } }
		};
	}

	private readonly HttpClient _client;

	public async Task FillDataAsync(List<Package> packages)
	{
		foreach (var package in packages)
		{
			var response = await _client
				.GetAsync($"/v3/registration5-gz-semver2/{package.Id.ToLower()}/index.json")
				.AsResponseAsync<JsonNode>();
			if (!response.IsSuccess) continue;

			var items = ((JsonArray?)response.Payload.Root["items"])?.WhereNotNull();
			//var pages = ((JsonArray?)response.Payload.Root["items"])?.WhereNotNull()
			//	.Select(n => n["@id"]?.GetValue<string>());
			if (items is null) continue;
			//if (pages is null) continue;

			foreach (var item in items)
			{
				var array = item["items"]?.AsArray();
				if (array is null)
				{
					var page = item["@id"]?.GetValue<string>();
					if (page.IsNullOrWhiteSpace()) continue;
					response = await _client.GetAsync(page).AsResponseAsync<JsonNode>();
					if (!response.IsSuccess) continue;
					array = response.Payload.Root["items"]?.AsArray();
					if (array is null) continue;
				}

				var datas = array.WhereNotNull()
					.Select(n => new
					{
						Version = n["catalogEntry"]?["version"]?.GetValue<string>(),
						Listed = n["catalogEntry"]?["listed"]?.GetValue<bool>(),
						Deprecated = n["catalogEntry"]?["deprecation"]?.AsObject() != null,
					});

				foreach (var data in datas)
				{
					if (data.Version.IsNullOrWhiteSpace() && data.Listed is null)
						package.Versions.Add(
							new(package, new("1.0.0-error.1"), data.Listed ?? true, false, NugetAction.Error));
					else if (data.Version.IsNullOrWhiteSpace())
						package.Versions.Add(
							new(package, new("1.0.0-error.1"), data.Listed ?? true, false, NugetAction.Error));
					else if (data.Listed is null)
						package.Versions.Add(new(package, new(data.Version!), false, false, NugetAction.Error));
					else
					{
						var version = new SemanticVersion(data.Version);
						var listed = data.Listed.Value;
						var deprecated = data.Deprecated;
						var action = NugetAction.None;

						if (!listed && package.ListMatch(version))
							action |= NugetAction.List;
						if (listed && !package.ListMatch(version))
							action |= NugetAction.Unlist;

						if (!deprecated && package.DeprecationMatch(version))
							action |= NugetAction.Deprecate;
						if (deprecated && !package.DeprecationMatch(version))
							action |= NugetAction.Undeprecate;

						package.Versions.Add(new(package, version, listed, deprecated, action));
					}
				}
			}
		}
	}
	public async Task<Response> List(PackageVersion version) =>
		await _client.PostAsync($"https://www.nuget.org/api/v2/package/{version.Package.Id}/{version.Version}", null).AsResponseAsync();

	public async Task<Response> Unlist(PackageVersion version) =>
		await _client.DeleteAsync($"https://www.nuget.org/api/v2/package/{version.Package.Id}/{version.Version}").AsResponseAsync();

	public async Task ApplyListUnlistAsync(List<Package> packages)
	{
		foreach (var package in packages)
		{
			foreach (var version in package.Versions.Where(v => v.Action.HasFlag(NugetAction.List)))
			{
				var response = await List(version);
				if(response.IsError)
					Console.WriteLine($"Error listing {version.Package.Id} {version.Version}: {response.Message}");
			}
			foreach (var version in package.Versions.Where(v => v.Action.HasFlag(NugetAction.Unlist)))
			{
				var response = await Unlist(version);
				if(response.IsError)
					Console.WriteLine($"Error unlisting {version.Package.Id} {version.Version}: {response.Message}");
			}
		}

		await FillDataAsync(packages);
	}
}