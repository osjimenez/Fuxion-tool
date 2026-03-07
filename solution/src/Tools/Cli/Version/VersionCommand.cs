using System;
using System.Threading;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Fuxion.Tools.Cli.Version;

public sealed class VersionCommand : Command<VersionCommandSettings>
{
	public override int Execute(CommandContext context, VersionCommandSettings settings, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(settings.OutputPath))
		{
			AnsiConsole.MarkupLine("[red]Missing[/] [yellow]--output[/].");
			return 2;
		}

		var config = FuxionToolsConfigLoader.LoadOrDefault(settings.ConfigPath);
		var inputs = VersioningInputsFactory.Create(config, settings.ConfigPath);

		if (!settings.NonInteractive)
		{
			AnsiConsole.Write(new Rule("Versioning"));

			var table = new Table().RoundedBorder().BorderColor(Color.Grey);
			table.AddColumn("Setting");
			table.AddColumn("Value");
			table.AddRow("Config", settings.ConfigPath);
			table.AddRow("Output", settings.OutputPath);
			table.AddRow("Mode", config.Versioning?.GetType().Name ?? "(default)");
			table.AddRow("BaseVersion", inputs.BaseVersion);
			table.AddRow("InformationalSuffix", inputs.InformationalSuffix);
			AnsiConsole.Write(table);

			var proceed = AnsiConsole.Confirm("Generate/refresh version props?", defaultValue: true);
			if (!proceed)
			{
				AnsiConsole.MarkupLine("[yellow]Cancelled[/].");
				return 0;
			}
		}

		var changed = VersioningOrchestrator.EnsurePropsUpToDate(settings.OutputPath, inputs);
		var props = DefaultVersionPropsProvider.FromInputs(inputs);

		if (settings.NonInteractive)
		{
			var projectName = settings.ProjectName
			                  ?? Environment.GetEnvironmentVariable("MSBuildProjectName")
			                  ?? "<unknown>";
			var targetFramework = Environment.GetEnvironmentVariable("TargetFramework")
				?? Environment.GetEnvironmentVariable("TARGET_FRAMEWORK");
			if (string.IsNullOrWhiteSpace(targetFramework))
			{
				var tfmId = Environment.GetEnvironmentVariable("TargetFrameworkIdentifier");
				var tfmVer = Environment.GetEnvironmentVariable("TargetFrameworkVersion");
				if (!string.IsNullOrWhiteSpace(tfmId) && !string.IsNullOrWhiteSpace(tfmVer))
					targetFramework = $"{tfmId}{tfmVer}";
			}
			targetFramework ??= "(unknown)";

			var fullVersion = props.InformationalVersion;
			Console.WriteLine($"[Info] - {projectName} ({targetFramework}) - Version: {fullVersion}");
			return 0;
		}

		if (!settings.NonInteractive)
		{
			AnsiConsole.Write(new Rule("Result"));

			var result = new Table().RoundedBorder().BorderColor(Color.Grey);
			result.AddColumn("Property");
			result.AddColumn("Value");
			result.AddRow("Written", changed ? "Yes" : "No (up-to-date)");
			result.AddRow("Version", props.Version);
			result.AddRow("PackageVersion", props.PackageVersion);
			result.AddRow("AssemblyVersion", props.AssemblyVersion);
			result.AddRow("FileVersion", props.FileVersion);
			result.AddRow("InformationalVersion", props.InformationalVersion);
			result.AddRow("Fingerprint", props.Fingerprint);
			AnsiConsole.Write(result);
		}

		return 0;
	}
}
