using System;
using System.IO;
using Fuxion.Tools.Core.Configuration;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary><c>fuxion-tools.json</c>, read with source-generated serialization (plan K, K2.4).</summary>
public sealed class ConfigTest : IDisposable
{
	readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fx-test-" + Guid.NewGuid().ToString("N")[..12])).FullName;

	public void Dispose() => Directory.Delete(_dir, true);

	FuxionToolsConfig Load(string json)
	{
		var path = Path.Combine(_dir, "fuxion-tools.json");
		File.WriteAllText(path, json);
		return FuxionToolsConfigLoader.LoadOrDefault(path);
	}

	[Fact(DisplayName = "git mode, as Fuxion-plus has it")]
	public void GitMode()
	{
		var config = Load("""
			{
				"versioning": {
					"mode": "git",
					"forceCIEnvironment": true,
					"failOnError": true,
					"repositoryPath": "..\\"
				},
				"staticMetadata": {
					"enabled": true,
					"namespace": "Fx.Metadata",
					"generation": "always"
				}
			}
			""");
		var git = Assert.IsType<GitVersioningConfig>(config.Versioning);
		Assert.True(git.ForceCIEnvironment);
		Assert.True(git.FailOnError);
		Assert.Equal(@"..\", git.RepositoryPath);
		Assert.Null(git.InformationalSuffix);
		Assert.NotNull(config.StaticMetadata);
		Assert.True(config.StaticMetadata.Enabled);
		Assert.Equal("Fx.Metadata", config.StaticMetadata.Namespace);
		Assert.Equal("always", config.StaticMetadata.Generation);
	}

	[Fact(DisplayName = "fixed mode")]
	public void FixedMode()
	{
		var config = Load("""{ "versioning": { "mode": "fixed", "version": "3.4.5", "informationalSuffix": "+dev" } }""");
		var fixedConfig = Assert.IsType<FixedVersioningConfig>(config.Versioning);
		Assert.Equal("3.4.5", fixedConfig.Version);
		Assert.Equal("+dev", fixedConfig.InformationalSuffix);
	}

	[Fact(DisplayName = "comments, trailing commas and any case in the names")]
	public void Lenient()
	{
		var config = Load("""
			{
				// comment
				"Versioning": { "mode": "git", "FailOnError": true, },
			}
			""");
		Assert.True(Assert.IsType<GitVersioningConfig>(config.Versioning).FailOnError);
	}

	[Theory(DisplayName = "a missing, empty or broken file: the default configuration")]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("{ not json")]
	[InlineData("""{ "versioning": { "mode": "other" } }""")]
	public void Default(string? json)
	{
		var config = json is null ? FuxionToolsConfigLoader.LoadOrDefault(Path.Combine(_dir, "missing.json")) : Load(json);
		Assert.Null(config.Versioning);
		Assert.Null(config.StaticMetadata);
	}
}
