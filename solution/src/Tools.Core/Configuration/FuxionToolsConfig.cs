using System.Text.Json.Serialization;

namespace Fuxion.Tools.Core.Configuration;

public sealed class FuxionToolsConfig
{
	[JsonPropertyName("versioning")]
	public VersioningConfig? Versioning { get; init; }

	[JsonPropertyName("staticMetadata")]
	public StaticMetadataConfig? StaticMetadata { get; init; }
}

public sealed class StaticMetadataConfig
{
	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }

	[JsonPropertyName("namespace")]
	public string? Namespace { get; init; }

	[JsonPropertyName("generation")]
	public string? Generation { get; init; }
}

/// <summary>
/// Versioning configuration root. Deserialized polymorphically by the <c>mode</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(FixedVersioningConfig), typeDiscriminator: "fixed")]
[JsonDerivedType(typeof(GitVersioningConfig), typeDiscriminator: "git")]
public abstract class VersioningConfig
{
	// Discriminator property is declared in JSON as "mode" by JsonPolymorphic.
}

public sealed class FixedVersioningConfig : VersioningConfig
{
	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("informationalSuffix")]
	public string? InformationalSuffix { get; init; }
}

public sealed class GitVersioningConfig : VersioningConfig
{
	[JsonPropertyName("informationalSuffix")]
	public string? InformationalSuffix { get; init; }

	[JsonPropertyName("forceCIEnvironment")]
	public bool ForceCIEnvironment { get; init; }

	[JsonPropertyName("failOnError")]
	public bool FailOnError { get; init; }

	[JsonPropertyName("repositoryPath")]
	public string? RepositoryPath { get; init; }
}
