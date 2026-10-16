using System.CommandLine;
using System.Threading;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Versioning;

namespace Fuxion.Tool.Cli.Version;

public sealed record VersionTagSettings(GlobalSettings Global, string MajorMinor);

/// <summary><c>fx version tag &lt;X.Y&gt;</c>: starts a new minor or major with the tag <c>version/X.Y.0</c> on HEAD.</summary>
public static class VersionTagCommand
{
	public const string Schema = "fx-version-tag/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		// optional for the parser: asked if missing (plan O, O3.3)
		var majorMinor = new Argument<string>("X.Y") { Description = Texts.Get("version.tag.argument"), Arity = ArgumentArity.ZeroOrOne };
		var command = new Command("tag", Texts.Get("version.tag"))
		{
			majorMinor
		}.Describe(Schema, writes: true);
		command.SetAction(result =>
		{
			var settings = new VersionTagSettings(global.Bind(result, currentDirectory), result.GetValue(majorMinor) ?? "");
			var console = new FxConsole(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings.Global);
			return console.Run(token => Run(settings, console, token));
		});
		return command;
	}

	public static int Run(VersionTagSettings settings, FxConsole console, CancellationToken cancellationToken = default)
	{
		CreatedVersionTag created;
		try
		{
			var majorMinor = settings.MajorMinor;
			if (string.IsNullOrWhiteSpace(majorMinor))
			{
				if (!console.Capabilities.CanPrompt)
					return console.Missing("<X.Y>", "fx version tag <X.Y>");
				// the next minor or the next major of the highest tag
				var (highestMajor, highestMinor) = VersionTagger.Highest(settings.Global.Directory) ?? (0, 0);
				string[] next = [$"{highestMajor}.{highestMinor + 1}", $"{highestMajor + 1}.0"];
				majorMinor = Ask.One(console, Texts.Get("version.tag.ask"), next,
					v => Texts.Get(v == next[0] ? "version.tag.next-minor" : "version.tag.next-major", v), cancellationToken);
				Ask.Equivalent(console, $"fx version tag {majorMinor}");
			}
			if (!VersionTagger.TryParseMajorMinor(majorMinor, out var major, out var minor))
				throw new VersioningException(VersionTagger.InvalidArgument, new(VersionTagger.InvalidArgument, majorMinor));
			created = VersionTagger.Create(settings.Global.Directory, major, minor);
		}
		catch (VersioningException ex)
		{
			FxDiagnostic[] errors = [ex.ToDiagnostic()];
			if (settings.Global.IsMachine)
				console.WriteJson(new VersionTagDocument(Schema, false, null, null, JsonDiagnostic.From(errors)), c => c.VersionTagDocument);
			return console.Report(errors);
		}

		if (settings.Global.IsMachine)
			console.WriteJson(new VersionTagDocument(Schema, true, created.Name, created.Commit, []), c => c.VersionTagDocument);
		else
			console.Line($"[green]✓[/] {FxConsole.Escape(Texts.Get("version.tag.created", created.Name, created.Commit[..7]))}");
		return 0;
	}
}
