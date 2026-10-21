using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Cli.Describe;
using Fuxion.Tool.Core.Harness;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The harness of coding agents (plan Q): rules and skills written once in <c>_fx/harness/</c>, which go down with what
/// is for each one, and the files each agent reads generated in every root of fx.
/// </summary>
public sealed class HarnessTest : SampleTree
{
	static string SkillMd(string name) => $"---\nname: {name}\ndescription: What {name} does and when to use it.\n---\n\n# {name}\n\nThe steps.\n";

	/// <summary>A repository on its own, a root of fx named solo.</summary>
	static TempGitRepository Solo()
	{
		var repo = new TempGitRepository();
		repo.WriteFile("_fx/space.yaml", "schema: 1.0.0\nname: solo\n");
		return repo;
	}

	static IReadOnlyList<FxModuleResult> Doctor(string directory)
		=> SyncEngine.Doctor(directory, new(Offline: true, Skill: FxSkill.Text), cancellationToken: TestContext.Current.CancellationToken);

	static IEnumerable<FxDiagnostic> All(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Diagnostics);

	static void Write(string root, string relative, string content)
	{
		var path = Path.Combine(root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact(DisplayName = "every root of fx gets AGENTS.md with the rule of fx, CLAUDE.md that reads it and the skill of fx, without harness.yaml")]
	public void BuiltIn()
	{
		using var solo = Solo();
		Assert.Empty(Errors(Sync(solo.Path)));
		var agents = Read(solo.Path, "AGENTS.md");
		Assert.Contains(FxHeader.Mark, agents);
		Assert.Contains(HarnessGenerator.FxRuleTitle, agents);
		Assert.Contains("This is `solo`, a repository with fx.", agents);
		// a repository on its own has no workspace to make worktrees in
		Assert.DoesNotContain("worktree", agents);
		Assert.EndsWith("\n@AGENTS.md\n", Read(solo.Path, "CLAUDE.md"));
		foreach (var root in HarnessModule.SkillRoots)
			Assert.Equal(FxSkill.Text, Read(solo.Path, $"{root}/fx/SKILL.md"));
		Assert.False(Directory.Exists(Path.Combine(solo.Path, ".claude", "rules")));
		Assert.Empty(Errors(Doctor(solo.Path)));
	}

	[Fact(DisplayName = "the rules and skills of harness.yaml: AGENTS.md in order, the rules by path in .claude/rules with an index, the skills with their files in both folders; what leaves the yaml goes")]
	public void RulesAndSkills()
	{
		using var solo = Solo();
		solo.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: first\n  - name: csharp\n    paths: [\"**/*.cs\", src/**]\n  - name: second\nskills:\n  - name: tool\n");
		solo.WriteFile("_fx/harness/rules/first.md", "## First\n\nOne.\n");
		solo.WriteFile("_fx/harness/rules/second.md", "\n\n## Second\n\nTwo.\n\n\n");
		solo.WriteFile("_fx/harness/rules/csharp.md", "## C#\n\nBraces.\n");
		solo.WriteFile("_fx/harness/skills/tool/SKILL.md", SkillMd("tool"));
		solo.WriteFile("_fx/harness/skills/tool/scripts/run.pl", "print 1;\n");
		Assert.Empty(Errors(Sync(solo.Path)));

		var agents = Read(solo.Path, "AGENTS.md");
		var order = new[] { HarnessGenerator.FxRuleTitle, "## First", "## Second", "## Rules by path" }.Select(t => agents.IndexOf(t, System.StringComparison.Ordinal)).ToList();
		Assert.DoesNotContain(-1, order);
		Assert.Equal(order.Order(), order);
		Assert.Contains("## Second\n\nTwo.\n\n## Rules by path", agents);
		Assert.Contains("- `.claude/rules/csharp.md`: `**/*.cs`, `src/**`\n", agents);
		Assert.DoesNotContain("Braces.", agents);
		var csharp = Read(solo.Path, ".claude/rules/csharp.md");
		Assert.StartsWith("---\npaths:\n  - \"**/*.cs\"\n  - src/**\n---\n", csharp);
		Assert.Contains(FxHeader.Mark, csharp);
		Assert.EndsWith("## C#\n\nBraces.\n", csharp);
		foreach (var root in HarnessModule.SkillRoots)
		{
			var skill = Read(solo.Path, $"{root}/tool/SKILL.md");
			Assert.StartsWith("---\nname: tool\n", skill);
			Assert.Contains(FxHeader.Mark, skill);
			Assert.EndsWith("# tool\n\nThe steps.\n", skill);
			Assert.Equal("print 1;\n", Read(solo.Path, $"{root}/tool/scripts/run.pl"));
		}
		Assert.Empty(Errors(Doctor(solo.Path)));

		// out of the yaml: what fx generated for them goes, the skill of fx stays
		solo.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: first\n");
		Assert.Empty(Errors(Sync(solo.Path)));
		Assert.False(File.Exists(Path.Combine(solo.Path, ".claude", "rules", "csharp.md")));
		foreach (var root in HarnessModule.SkillRoots)
		{
			Assert.False(Directory.Exists(Path.Combine(solo.Path, root, "tool")));
			Assert.True(File.Exists(Path.Combine(solo.Path, root, "fx", "SKILL.md")));
		}
		Assert.DoesNotContain("## Rules by path", Read(solo.Path, "AGENTS.md"));
		// what is left in _fx/harness/ goes nowhere: the doctor says so
		Assert.Equal(3, All(Doctor(solo.Path)).Count(d => d.Code == HarnessModule.Undeclared && d.Severity == FxSeverity.Warning));
	}

	[Fact(DisplayName = "the harness goes down with what is for each one: applies-to filters the root too, a workspace passes on what may be below, a repository gets only what is its")]
	public void Inheritance()
	{
		var acme = Clone(_acmeOrigin);
		acme.WriteFile("_fx/harness/harness.yaml", """
			schema: 1.0.0
			rules:
			  - name: company
			  - name: acme-only
			    applies-to:
			      repositories: [/acme]
			  - name: apps
			    applies-to:
			      tags: [app]
			  - name: w1-only
			    applies-to:
			      repositories: [repo-w1]
			  - name: csharp
			    paths: ["**/*.cs"]
			    applies-to:
			      tags: [dotnet]
			skills:
			  - name: tool
			    applies-to:
			      tags: [dotnet]
			""");
		foreach (var rule in new[] { "company", "acme-only", "apps", "w1-only", "csharp" })
			acme.WriteFile($"_fx/harness/rules/{rule}.md", $"## {rule}\n\nThe rule {rule}.\n");
		acme.WriteFile("_fx/harness/skills/tool/SKILL.md", SkillMd("tool"));
		Assert.Empty(Errors(Sync(acme.Path)));

		string Agents(string repo) => Read(Path.Combine(acme.Path, repo), "AGENTS.md");
		bool Inherited(string repo, string file) => File.Exists(Path.Combine(acme.Path, repo, "_fx", ".inherited", "harness", file));
		bool Skill(string repo) => File.Exists(Path.Combine(acme.Path, repo, ".claude", "skills", "tool", "SKILL.md"));

		// acme: what has no applies-to and what names it; not what goes by tags it does not have
		var root = Agents(".");
		Assert.Contains(HarnessGenerator.FxWorkspaceRuleTitle, root);
		Assert.Contains("The rule company.", root);
		Assert.Contains("The rule acme-only.", root);
		Assert.DoesNotContain("The rule apps.", root);
		Assert.False(Skill("."));
		// repo-w1, a workspace (dotnet, product): what is for it, and it passes on what may be below; never what is only acme's
		var w1 = Agents("repo-w1");
		Assert.Contains("The rule w1-only.", w1);
		Assert.DoesNotContain("The rule apps.", w1);
		Assert.Contains("inside the workspace `acme`", w1);
		Assert.True(Inherited("repo-w1", "rules/apps.md"));
		Assert.False(Inherited("repo-w1", "rules/acme-only.md"));
		Assert.True(Skill("repo-w1"));
		// repo-c1 (dotnet, app): only what is for it reaches it
		var c1 = Agents("repo-w1/repo-c1");
		Assert.Contains("a repository of the workspace `repo-w1`", c1);
		Assert.Contains("The rule company.", c1);
		Assert.Contains("The rule apps.", c1);
		Assert.Contains("`.claude/rules/csharp.md`: `**/*.cs`", c1);
		Assert.DoesNotContain("The rule w1-only.", c1);
		Assert.True(Inherited("repo-w1/repo-c1", "rules/apps.md"));
		Assert.False(Inherited("repo-w1/repo-c1", "rules/w1-only.md"));
		Assert.False(Inherited("repo-w1/repo-c1", "rules/acme-only.md"));
		Assert.True(Inherited("repo-w1/repo-c1", "skills/tool/SKILL.md"));
		Assert.True(Skill("repo-w1/repo-c1"));
		// repo-c2 (dotnet, mobile): not the rule of the apps
		Assert.DoesNotContain("The rule apps.", Agents("repo-w1/repo-c2"));
		Assert.False(Inherited("repo-w1/repo-c2", "rules/apps.md"));
		Assert.Empty(Errors(Doctor(acme.Path)));
	}

	[Fact(DisplayName = "a rule of a repository with the name of one it inherits replaces it")]
	public void NearestWins()
	{
		var acme = Clone(_acmeOrigin);
		acme.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: company\n");
		acme.WriteFile("_fx/harness/rules/company.md", "## Company\n\nFrom acme.\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		var shared = Path.Combine(acme.Path, "libs", "shared");
		Assert.Contains("From acme.", Read(shared, "AGENTS.md"));

		Write(shared, "_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: company\n");
		Write(shared, "_fx/harness/rules/company.md", "## Company\n\nFrom shared.\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		var agents = Read(shared, "AGENTS.md");
		Assert.Contains("From shared.", agents);
		Assert.DoesNotContain("From acme.", agents);
	}

	[Fact(DisplayName = "what is in the files of the agents and is not fx's is an error, untouched; --adopt moves it to _fx/harness/, declares it and generates")]
	public void ForeignAndAdopt()
	{
		using var solo = Solo();
		// one that talks about the header is not fx's: only the header in its place counts
		solo.WriteFile("AGENTS.md", "# Mine\n\nMy rules.\n\nThe files with `GENERATED by Fuxion.Tool (fx) - DO NOT EDIT` are fx's.\n");
		solo.WriteFile("CLAUDE.md", "@AGENTS.md\n");
		solo.WriteFile(".claude/rules/old.md", "---\npaths:\n  - \"**/*.sql\"\n---\n\n## Old\n\nSQL.\n");
		solo.WriteFile(".claude/skills/mine/SKILL.md", SkillMd("mine"));
		solo.WriteFile(".claude/skills/mine/helper.txt", "help\n");
		var errors = Errors(Sync(solo.Path)).Select(e => e.Code).ToList();
		Assert.Contains(FxOutputs.NotGenerated, errors);
		Assert.Equal(2, errors.Count(c => c == HarnessModule.Foreign));
		Assert.StartsWith("# Mine\n\nMy rules.\n", Read(solo.Path, "AGENTS.md"));
		Assert.Equal("help\n", Read(solo.Path, ".claude/skills/mine/helper.txt"));

		Assert.Empty(Errors(Sync(solo.Path, new(Adopt: true))));
		Assert.StartsWith("# Mine\n\nMy rules.\n", Read(solo.Path, "_fx/harness/rules/agents-adopted.md"));
		Assert.Equal("## Old\n\nSQL.\n", Read(solo.Path, "_fx/harness/rules/old.md"));
		Assert.Equal(SkillMd("mine"), Read(solo.Path, "_fx/harness/skills/mine/SKILL.md"));
		Assert.Equal("help\n", Read(solo.Path, "_fx/harness/skills/mine/helper.txt"));
		// a CLAUDE.md that only read AGENTS.md has nothing to adopt
		Assert.False(File.Exists(Path.Combine(solo.Path, "_fx", "harness", "rules", "claude-adopted.md")));
		var config = HarnessYamlReader.ReadFile(Path.Combine(solo.Path, "_fx", "harness", "harness.yaml"));
		Assert.Equal(["agents-adopted", "old"], config.Rules.Select(r => r.Name));
		Assert.Equal(["**/*.sql"], config.Rules[1].Paths);
		Assert.Equal(["mine"], config.Skills.Select(s => s.Name));

		var agents = Read(solo.Path, "AGENTS.md");
		Assert.Contains(FxHeader.Mark, agents);
		Assert.Contains("My rules.", agents);
		Assert.Contains(FxHeader.Mark, Read(solo.Path, ".claude/rules/old.md"));
		Assert.Contains(FxHeader.Mark, Read(solo.Path, ".agents/skills/mine/SKILL.md"));
		Assert.Equal("help\n", Read(solo.Path, ".agents/skills/mine/helper.txt"));
		Assert.Empty(Errors(Doctor(solo.Path)));
	}

	[Fact(DisplayName = "the problems: out of date, a rule without its file, a skill with another name, an invalid harness.yaml with its lines")]
	public void Problems()
	{
		using var solo = Solo();
		solo.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: first\n");
		solo.WriteFile("_fx/harness/rules/first.md", "## First\n");
		Assert.Empty(Errors(Sync(solo.Path)));
		Assert.Empty(Errors(Doctor(solo.Path)));

		solo.WriteFile("_fx/harness/rules/first.md", "## First, changed\n");
		Assert.Contains(Errors(Doctor(solo.Path)), d => d.Code == HarnessModule.Outdated && d.File == "AGENTS.md");

		solo.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: first\n  - name: gone\nskills:\n  - name: tool\n");
		solo.WriteFile("_fx/harness/skills/tool/SKILL.md", SkillMd("other"));
		var errors = Errors(Sync(solo.Path)).ToList();
		Assert.Contains(errors, d => d.Code == HarnessModule.MissingRule && d.Line == 4);
		Assert.Contains(errors, d => d.Code == HarnessModule.InvalidSkill && d.Message.Key == $"{HarnessModule.InvalidSkill}.name");
		Assert.False(File.Exists(Path.Combine(solo.Path, ".claude", "skills", "tool", "SKILL.md")));
		Assert.Contains("## First, changed", Read(solo.Path, "AGENTS.md"));

		solo.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: fx\n  - name: first\n  - name: first\nskills: tool\n");
		var invalid = Errors(Sync(solo.Path)).ToList();
		Assert.Contains(invalid, d => d.Message.Key == $"{HarnessYamlReader.InvalidYaml}.reserved-name" && d.Line == 3);
		Assert.Contains(invalid, d => d.Message.Key == $"{HarnessYamlReader.InvalidYaml}.duplicate-rule");
		Assert.Contains(invalid, d => d.Message.Key == $"{HarnessYamlReader.InvalidYaml}.list" && d.Line == 6);
	}

	[Fact(DisplayName = "--dry-run says what it would write and writes nothing")]
	public void DryRun()
	{
		using var solo = Solo();
		var results = Sync(solo.Path, new(DryRun: true));
		Assert.Contains(results.SelectMany(r => r.Files), f => f.Path == "AGENTS.md" && f.Status == SyncFileStatus.WouldWrite);
		Assert.False(File.Exists(Path.Combine(solo.Path, "AGENTS.md")));
		Assert.False(Directory.Exists(Path.Combine(solo.Path, ".claude")));
	}
}
