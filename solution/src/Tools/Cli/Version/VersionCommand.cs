using System.CommandLine;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Versioning;
using Spectre.Console;

namespace Fuxion.Tools.Cli.Version;

public sealed record VersionSettings(GlobalSettings Global, bool Explain);

/// <summary><c>fx version [--explain]</c>: the version the repository would build now (design §9, §10.2).</summary>
public static class VersionCommand
{
	public const string Schema = "fx-version/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var explain = new Option<bool>("--explain") { Description = "Show how the version was calculated." };
		var command = new Command("version", "The version this repository would build now, from its git history.")
		{
			explain,
			VersionTagCommand.Create(global, currentDirectory)
		};
		command.SetAction(result =>
		{
			var settings = new VersionSettings(global.Bind(result, currentDirectory), result.GetValue(explain));
			var console = new FxConsole(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings.Global);
			return Run(settings, console);
		});
		return command;
	}

	public static int Run(VersionSettings settings, FxConsole console)
	{
		VersioningResult result;
		try
		{
			result = VersioningInputsFactory.Calculate(new FuxionToolsConfig
			{
				Versioning = new GitVersioningConfig { RepositoryPath = settings.Global.Directory, FailOnError = true }
			});
		}
		catch (VersioningException ex)
		{
			FxDiagnostic[] errors = [ex.ToDiagnostic()];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(new VersionDocument(Schema, false, null, null, null, null, null, null, JsonDiagnostic.From(errors)), FxConsole.JsonContext.VersionDocument);
			return console.Report(errors);
		}

		var props = DefaultVersionPropsProvider.FromInputs(result.Inputs);
		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(Document(props, result.Details), FxConsole.JsonContext.VersionDocument);
		else if (settings.Explain)
			WriteExplanation(console.Out, props, result.Details);
		else
			console.Out.WriteLine(props.Version);
		return 0;
	}

	static VersionDocument Document(VersionProps props, GitVersionDetails? details) => new(
		Schema,
		true,
		props.Version,
		props.PackageVersion,
		props.AssemblyVersion,
		props.FileVersion,
		props.InformationalVersion,
		details is null
			? null
			: new JsonGit(details.Repository, details.Branch, RuleName(details.Rule), details.Head, details.StableTip, details.MergeBase,
				new JsonTag(details.TagName, details.TagCommit), details.CommitsSinceTag, details.BranchCommits),
		[]);

	static void WriteExplanation(IAnsiConsole console, VersionProps props, GitVersionDetails? details)
	{
		var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
		void Row(string name, string value) => grid.AddRow(new Spectre.Console.Text(name, new Style(decoration: Decoration.Bold)), new Spectre.Console.Text(value));

		Row("Version", props.Version);
		Row("Informational", props.InformationalVersion);
		Row("Assembly", props.AssemblyVersion);
		if (details is not null)
		{
			Row("Repository", details.Repository);
			Row("Branch", details.Branch);
			Row("Rule", $"{RuleName(details.Rule)}: {RuleDescription(details.Rule)}");
			Row("HEAD", Short(details.Head));
			Row("Tag", $"{details.TagName} at {Short(details.TagCommit)}");
			if (details.Rule == GitVersionRule.Feature)
			{
				Row("Merge base", $"{Short(details.MergeBase!)} with {Short(details.StableTip)}");
				Row("Tag → merge base", $"{details.CommitsSinceTag} commits");
				Row("On the branch", $"{details.BranchCommits} commits since main was last merged");
			}
			else
				Row("Tag → HEAD", $"{details.CommitsSinceTag} commits");
		}
		console.Write(grid);
	}

	public static string RuleName(GitVersionRule rule) => rule switch
	{
		GitVersionRule.Stable => "stable",
		GitVersionRule.Develop => "develop",
		GitVersionRule.Feature => "feature",
		GitVersionRule.Release => "release",
		GitVersionRule.Preview => "preview",
		_ => "other"
	};

	static string RuleDescription(GitVersionRule rule) => rule switch
	{
		GitVersionRule.Stable => "X.Y.{commits since the tag}",
		GitVersionRule.Develop => "X.Y.{commits since the tag}-alpha",
		GitVersionRule.Feature => "X.Y.{tag → merge base}-feature.{name}.{commits since main was last merged}",
		GitVersionRule.Release => "{tag}-rc.{name}.{commits since the tag}",
		GitVersionRule.Preview => "{tag}-preview.{name}.{commits since the tag}",
		_ => "X.Y.{commits since the tag}-{branch}"
	};

	static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}
