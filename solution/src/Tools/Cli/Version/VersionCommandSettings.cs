using Spectre.Console.Cli;

namespace Fuxion.Tools.Cli.Version;

public sealed class VersionCommandSettings : CommandSettings
{
	[CommandOption("--output <PATH>")]
	public required string OutputPath { get; init; }

	[CommandOption("--config <PATH>")]
	public string ConfigPath { get; init; } = "fuxion-tools.json";

	[CommandOption("--non-interactive")]
	public bool NonInteractive { get; init; }

	[CommandOption("--project-name <NAME>")]
	public string? ProjectName { get; init; }
}
