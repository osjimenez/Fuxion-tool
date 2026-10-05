using System.CommandLine;
using Fuxion.Tools.Core.Versioning;
using Spectre.Console;

namespace Fuxion.Tools.Cli.Version;

public sealed record VersionTagSettings(GlobalSettings Global, string MajorMinor);

/// <summary><c>fx version tag &lt;X.Y&gt;</c>: starts a new minor or major with the tag <c>version/X.Y.0</c> on HEAD.</summary>
public static class VersionTagCommand
{
	public const string Schema = "fx-version-tag/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var majorMinor = new Argument<string>("X.Y") { Description = "The new major and minor, like 11.3." };
		var command = new Command("tag", "Create the tag version/X.Y.0 on HEAD to start a new minor or major (local; push it yourself).")
		{
			majorMinor
		};
		command.SetAction(result =>
		{
			var settings = new VersionTagSettings(global.Bind(result, currentDirectory), result.GetValue(majorMinor) ?? "");
			var console = new FxConsole(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings.Global);
			return Run(settings, console);
		});
		return command;
	}

	public static int Run(VersionTagSettings settings, FxConsole console)
	{
		CreatedVersionTag created;
		try
		{
			if (!VersionTagger.TryParseMajorMinor(settings.MajorMinor, out var major, out var minor))
				throw new VersioningException(VersionTagger.InvalidArgument, $"'{settings.MajorMinor}' is not X.Y (two numbers, like 11.3).");
			created = VersionTagger.Create(settings.Global.Directory, major, minor);
		}
		catch (VersioningException ex)
		{
			Diagnostic[] errors = [Diagnostic.Error(ex.Code, ex.Message)];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(Schema, errors);
			return console.Report(errors);
		}

		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(Schema, [], json =>
			{
				json.WriteString("tag", created.Name);
				json.WriteString("commit", created.Commit);
			});
		else
			console.Out.WriteLine($"Created {created.Name} on {created.Commit[..7]}. It is local: git push origin {created.Name}");
		return 0;
	}
}
