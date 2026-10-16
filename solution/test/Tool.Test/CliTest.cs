using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Versioning;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The fx command line, run in process (plan K, K2.3).</summary>
[Collection(nameof(ProcessCollection))]
public sealed class CliTest
{
	sealed record Run(int ExitCode, string Out, string Err)
	{
		public JsonElement Json => JsonDocument.Parse(Out).RootElement;
	}

	static Run Fx(string currentDirectory, params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = InProcess.Run(args, stdout, stderr, currentDirectory);
		return new(exitCode, stdout.ToString(), stderr.ToString());
	}

	static TempGitRepository FeatureRepo()
	{
		var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/1.2.0");
		repo.Commit("c2");
		repo.Branch("feature/x");
		repo.Commit("f1");
		return repo;
	}

	// fx version

	[Fact(DisplayName = "version: prints the version")]
	public void Version()
	{
		using var ci = CiEnvironment.Local();
		using var repo = FeatureRepo();
		var run = Fx(repo.Path, "version");
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("1.2.1-feature.x.1", run.Out.Trim());
		Assert.Equal("", run.Err);
	}

	[Fact(DisplayName = "version: --root instead of the current directory, also a subfolder")]
	public void Version_Root()
	{
		using var ci = CiEnvironment.Local();
		using var repo = FeatureRepo();
		var sub = Directory.CreateDirectory(Path.Combine(repo.Path, "solution")).FullName;
		var run = Fx(Path.GetTempPath(), "version", "--root", sub);
		Assert.Equal("1.2.1-feature.x.1", run.Out.Trim());
	}

	[Fact(DisplayName = "version --explain: rule, tag and counts")]
	public void Version_Explain()
	{
		using var ci = CiEnvironment.Local();
		using var repo = FeatureRepo();
		var run = Fx(repo.Path, "version", "--explain", "--plain");
		Assert.Equal(0, run.ExitCode);
		Assert.Contains("1.2.1-feature.x.1+local", run.Out);
		Assert.Contains("feature/x", run.Out);
		Assert.Contains("version/1.2.0", run.Out);
		Assert.Contains("1 commits since main was last merged", run.Out);
	}

	[Fact(DisplayName = "version --output json: the fx-version/1 document")]
	public void Version_Json()
	{
		using var ci = CiEnvironment.Local();
		using var repo = FeatureRepo();
		var json = Fx(repo.Path, "version", "--output", "json").Json;
		Assert.Equal("fx-version/1", json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("ok").GetBoolean());
		Assert.Equal("1.2.1-feature.x.1", json.GetProperty("version").GetString());
		Assert.Equal("1.2.1.0", json.GetProperty("assemblyVersion").GetString());
		Assert.Equal("1.2.1-feature.x.1+local", json.GetProperty("informationalVersion").GetString());
		var git = json.GetProperty("git");
		Assert.Equal("feature/x", git.GetProperty("branch").GetString());
		Assert.Equal("feature", git.GetProperty("rule").GetString());
		Assert.Equal("version/1.2.0", git.GetProperty("tag").GetProperty("name").GetString());
		Assert.Equal(1, git.GetProperty("commitsSinceTag").GetInt32());
		Assert.Equal(1, git.GetProperty("branchCommits").GetInt32());
		Assert.Equal(0, json.GetProperty("diagnostics").GetArrayLength());
	}

	[Fact(DisplayName = "version without a tag: error with its code, exit 1")]
	public void Version_NoTag()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		var run = Fx(repo.Path, "version");
		Assert.Equal(1, run.ExitCode);
		Assert.Equal("", run.Out);
		Assert.StartsWith("error version.no-tag: ", run.Err);
	}

	[Fact(DisplayName = "version outside a repository, in json: ok false and the diagnostic")]
	public void Version_NotARepository_Json()
	{
		using var folder = new TempGitRepository(init: false);
		var run = Fx(folder.Path, "version", "--output", "json");
		Assert.Equal(1, run.ExitCode);
		Assert.False(run.Json.GetProperty("ok").GetBoolean());
		var diagnostic = run.Json.GetProperty("diagnostics")[0];
		Assert.Equal("version.repository-not-found", diagnostic.GetProperty("code").GetString());
		Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
		Assert.Equal("", run.Err);
	}

	// fx version tag

	[Fact(DisplayName = "version tag: creates version/X.Y.0 on HEAD")]
	public void Tag()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/1.2.0");
		var head = repo.Commit("c2");
		var run = Fx(repo.Path, "version", "tag", "1.3", "--output", "json");
		Assert.Equal(0, run.ExitCode);
		Assert.Equal("version/1.3.0", run.Json.GetProperty("tag").GetString());
		Assert.Equal(head, run.Json.GetProperty("commit").GetString());
		Assert.Equal(head, repo.Git("rev-parse", "version/1.3.0^{commit}").Trim());
		Assert.Equal("1.3.0", Fx(repo.Path, "version").Out.Trim());
	}

	[Fact(DisplayName = "version tag: an existing tag is an error")]
	public void Tag_Exists()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/1.3.0");
		var run = Fx(repo.Path, "version", "tag", "1.3");
		Assert.Equal(1, run.ExitCode);
		Assert.StartsWith("error version.tag-exists: ", run.Err);
	}

	[Fact(DisplayName = "version tag: a version that is not newer is an error")]
	public void Tag_NotNewer()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/2.0.0");
		var run = Fx(repo.Path, "version", "tag", "1.9");
		Assert.Equal(1, run.ExitCode);
		Assert.StartsWith("error version.tag-not-newer: ", run.Err);
	}

	[Theory(DisplayName = "version tag: the argument must be X.Y")]
	[InlineData("1")]
	[InlineData("1.2.3")]
	[InlineData("1.x")]
	public void Tag_InvalidArgument(string value)
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		var run = Fx(repo.Path, "version", "tag", value);
		Assert.Equal(1, run.ExitCode);
		Assert.StartsWith("error version.invalid-argument: ", run.Err);
		Assert.Equal("", repo.Git("tag", "--list"));
	}

	[Fact(DisplayName = "version tag without X.Y and no terminal to ask for it: what is missing, exit code 2 (plan O, O3.3)")]
	public void Tag_Missing()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		var run = Fx(repo.Path, "version", "tag");
		Assert.Equal(FxConsole.UsageError, run.ExitCode);
		Assert.Contains("fx version tag <X.Y>", run.Err);
		Assert.Equal("", repo.Git("tag", "--list"));
	}

	[Fact(DisplayName = "version tag: the highest version tag, from which fx proposes the next minor and major")]
	public void Tag_Highest()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		Assert.Null(VersionTagger.Highest(repo.Path));
		repo.Tag("version/1.2.0");
		repo.Commit("c2");
		repo.Tag("version/1.10.0");
		repo.Tag("other/9.0.0");
		Assert.Equal((1, 10), VersionTagger.Highest(repo.Path));
	}

	// Parsing

	[Fact(DisplayName = "an unknown --output value is a usage error: exit code 2 and where the help is")]
	public void Parse_InvalidOutput()
	{
		var run = Fx(Path.GetTempPath(), "version", "--output", "xml");
		Assert.Equal(FxConsole.UsageError, run.ExitCode);
		Assert.Contains("'xml'", run.Err);
		Assert.Contains("fx --help", run.Err);
	}

	[Fact(DisplayName = "an unknown command is a usage error")]
	public void Parse_UnknownCommand()
	{
		var run = Fx(Path.GetTempPath(), "nosuch");
		Assert.Equal(FxConsole.UsageError, run.ExitCode);
		Assert.Contains("'nosuch'", run.Err);
	}

	[Fact(DisplayName = "fx alone and fx --help: fx's help, with every command and the global options (plan O, O3.3)")]
	public void Help()
	{
		var alone = Fx(Path.GetTempPath());
		Assert.Equal(0, alone.ExitCode);
		Assert.Equal("", alone.Err);
		Assert.Contains("Usage: fx <command> [options]", alone.Out);
		Assert.Contains("  repo mount [<names>...]", alone.Out);
		Assert.Contains("  version tag [<X.Y>]", alone.Out);
		Assert.Contains("  -v, --verbose", alone.Out);
		Assert.Contains("  --output <format>", alone.Out);
		// generated from the commands: none is missing; fx ui, at the top (plan O, O4)
		foreach (var command in Commands(FxApp.Build(Path.GetTempPath()), ""))
			Assert.Contains(command == "ui" ? "fx ui · " : $"  {command}", alone.Out);
		Assert.True(alone.Out.IndexOf("fx ui · ", StringComparison.Ordinal) < alone.Out.IndexOf("Usage:", StringComparison.Ordinal));

		var help = Fx(Path.GetTempPath(), "--help");
		Assert.Equal(0, help.ExitCode);
		Assert.Equal(alone.Out, help.Out);
		// the help of a command is System.CommandLine's
		Assert.Contains("--dry-run", Fx(Path.GetTempPath(), "sync", "--help").Out);
	}

	/// <summary>Every command that does something, with its path (<c>repo mount</c>).</summary>
	static IEnumerable<string> Commands(Command command, string parent)
	{
		foreach (var sub in command.Subcommands)
		{
			var path = parent.Length == 0 ? sub.Name : $"{parent} {sub.Name}";
			if (sub.Action is not null)
				yield return path;
			foreach (var inner in Commands(sub, path))
				yield return inner;
		}
	}
}
