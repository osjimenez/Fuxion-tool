using System.CommandLine;
using System.Text.Json;
using Fuxion.Tools.Core.Configuration;
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
			Diagnostic[] errors = [Diagnostic.Error(ex.Code, ex.Message)];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(Schema, errors);
			return console.Report(errors);
		}

		var props = DefaultVersionPropsProvider.FromInputs(result.Inputs);
		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(Schema, [], json => WriteJson(json, props, result.Details));
		else if (settings.Explain)
			WriteExplanation(console.Out, props, result.Details);
		else
			console.Out.WriteLine(props.Version);
		return 0;
	}

	static void WriteJson(Utf8JsonWriter json, VersionProps props, GitVersionDetails? details)
	{
		json.WriteString("version", props.Version);
		json.WriteString("packageVersion", props.PackageVersion);
		json.WriteString("assemblyVersion", props.AssemblyVersion);
		json.WriteString("fileVersion", props.FileVersion);
		json.WriteString("informationalVersion", props.InformationalVersion);
		if (details is null)
			return;
		json.WriteStartObject("git");
		json.WriteString("repository", details.Repository);
		json.WriteString("branch", details.Branch);
		json.WriteString("rule", RuleName(details.Rule));
		json.WriteString("head", details.Head);
		json.WriteString("stableTip", details.StableTip);
		if (details.MergeBase is null)
			json.WriteNull("mergeBase");
		else
			json.WriteString("mergeBase", details.MergeBase);
		json.WriteStartObject("tag");
		json.WriteString("name", details.TagName);
		json.WriteString("commit", details.TagCommit);
		json.WriteEndObject();
		json.WriteNumber("commitsSinceTag", details.CommitsSinceTag);
		if (details.BranchCommits is { } branchCommits)
			json.WriteNumber("branchCommits", branchCommits);
		else
			json.WriteNull("branchCommits");
		json.WriteEndObject();
	}

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
