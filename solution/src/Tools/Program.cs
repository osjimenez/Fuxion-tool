using System;
using Fuxion.Tools.Cli.Metadata;
using Fuxion.Tools.Cli.Version;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
	config.SetApplicationName("fuxion-tools");
	config.SetApplicationVersion("0.1.0");

	config.AddCommand<VersionCommand>("version")
		.WithDescription("Generate MSBuild props file with version information.");

	config.AddCommand<MetadataCommand>("metadata")
		.WithDescription("Generate per-project static metadata source file.");
});

try
{
	return app.Run(args);
}
catch (Exception ex)
{
	AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything | ExceptionFormats.ShowLinks);
	return 1;
}
