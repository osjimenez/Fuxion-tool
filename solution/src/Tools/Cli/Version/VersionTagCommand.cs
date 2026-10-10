using System.CommandLine;
using Fuxion.Tools.Core.Results;
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
		var majorMinor = new Argument<string>("X.Y") { Description = Texts.Get("version.tag.argument") };
		var command = new Command("tag", Texts.Get("version.tag"))
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
				throw new VersioningException(VersionTagger.InvalidArgument, new(VersionTagger.InvalidArgument, settings.MajorMinor));
			created = VersionTagger.Create(settings.Global.Directory, major, minor);
		}
		catch (VersioningException ex)
		{
			FxDiagnostic[] errors = [ex.ToDiagnostic()];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(new VersionTagDocument(Schema, false, null, null, JsonDiagnostic.From(errors)), FxConsole.JsonContext.VersionTagDocument);
			return console.Report(errors);
		}

		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(new VersionTagDocument(Schema, true, created.Name, created.Commit, []), FxConsole.JsonContext.VersionTagDocument);
		else
			console.Out.WriteLine(Texts.Get("version.tag.created", created.Name, created.Commit[..7]));
		return 0;
	}
}
