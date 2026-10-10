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
		var explain = new Option<bool>("--explain") { Description = Texts.Get("version.explain") };
		var command = new Command("version", Texts.Get("version"))
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

		Row(Texts.Get("version.row.version"), props.Version);
		Row(Texts.Get("version.row.informational"), props.InformationalVersion);
		Row(Texts.Get("version.row.assembly"), props.AssemblyVersion);
		if (details is not null)
		{
			Row(Texts.Get("version.row.repository"), details.Repository);
			Row(Texts.Get("version.row.branch"), details.Branch);
			Row(Texts.Get("version.row.rule"), $"{RuleName(details.Rule)}: {Texts.Get($"version.rule.{RuleName(details.Rule)}")}");
			Row(Texts.Get("version.row.head"), Short(details.Head));
			Row(Texts.Get("version.row.tag"), Texts.Get("version.tag-at", details.TagName, Short(details.TagCommit)));
			if (details.Rule == GitVersionRule.Feature)
			{
				Row(Texts.Get("version.row.merge-base"), Texts.Get("version.merge-base-with", Short(details.MergeBase!), Short(details.StableTip)));
				Row(Texts.Get("version.row.tag-to-merge-base"), Texts.Get("version.commits", details.CommitsSinceTag));
				Row(Texts.Get("version.row.on-the-branch"), Texts.Get("version.branch-commits", details.BranchCommits));
			}
			else
				Row(Texts.Get("version.row.tag-to-head"), Texts.Get("version.commits", details.CommitsSinceTag));
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

	static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}
