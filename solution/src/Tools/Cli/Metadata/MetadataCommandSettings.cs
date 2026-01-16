using Spectre.Console.Cli;

namespace Fuxion.Tools.Cli.Metadata;

public sealed class MetadataCommandSettings : CommandSettings
{
	[CommandOption("--output <PATH>")]
	public required string OutputPath { get; init; }

	[CommandOption("--project-name <NAME>")]
	public string? ProjectName { get; init; }

	[CommandOption("--config <PATH>")]
	public string ConfigPath { get; init; } = "fuxion-tools.json";

	[CommandOption("--non-interactive")]
	public bool NonInteractive { get; init; }
}
