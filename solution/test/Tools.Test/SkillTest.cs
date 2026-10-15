using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Cli.Describe;
using Fuxion.Tools.Core.Harness;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The skill of fx in every root of fx (plan O, O5.1): so that a coding agent working in it knows how to use fx.</summary>
public sealed class SkillTest : SampleTree
{
	static (int ExitCode, string Out, string Err) Fx(string root, params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = FxApp.Run(args, stdout, stderr, root);
		return (exitCode, stdout.ToString(), stderr.ToString());
	}

	static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

	[Fact(DisplayName = "fx sync writes the skill of fx in every root of fx, versioned; the doctor says when it is out of date; one of the user's own is not overwritten")]
	public void EveryRoot()
	{
		var acme = AcmeCommitted();
		Assert.Equal(0, Fx(acme.Path, "sync").ExitCode);
		foreach (var root in Repositories)
			foreach (var folder in HarnessModule.SkillRoots)
				Assert.Equal(FxSkill.Text, Read(Path.Combine(acme.Path, root, folder, "fx", "SKILL.md")));
		// a workspace versions them (its .gitignore is a whitelist)
		Assert.Contains("!/.claude/skills/**", Read(Path.Combine(acme.Path, ".gitignore")));
		Assert.Contains("!/.agents/skills/**", Read(Path.Combine(acme.Path, "repo-w1", ".gitignore")));
		// the same skill as fx describe --skill
		Assert.Equal(FxSkill.Text.TrimEnd('\n'), Fx(acme.Path, "describe", "--skill").Out.Replace("\r\n", "\n").TrimEnd('\n'));

		// out of date: the doctor says so, and sync puts it back
		var c1 = Path.Combine(acme.Path, "repo-w1", "repo-c1", ".claude", "skills", "fx", "SKILL.md");
		File.WriteAllText(c1, FxSkill.Text.Replace("Never `fx ui`", "Never fx ui"));
		var doctor = JsonDocument.Parse(Fx(acme.Path, "doctor", "--output", "json", "--offline").Out).RootElement;
		Assert.Contains(doctor.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == HarnessModule.Outdated
		                                                                        && d.GetProperty("file").GetString()!.EndsWith("repo-c1/.claude/skills/fx/SKILL.md"));
		Assert.Equal(0, Fx(acme.Path, "sync").ExitCode);
		Assert.Equal(FxSkill.Text, Read(c1));

		// one of the user's own (without the header of fx): not overwritten
		var c2 = Path.Combine(acme.Path, "repo-w1", "repo-c2", ".agents", "skills", "fx", "SKILL.md");
		File.WriteAllText(c2, "mine\n");
		var refused = Fx(acme.Path, "sync", "--output", "json");
		Assert.Equal(1, refused.ExitCode);
		Assert.Contains(FxOutputs.NotGenerated, refused.Out);
		Assert.Equal("mine\n", Read(c2));
	}
}
