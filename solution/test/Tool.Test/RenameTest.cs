using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>fx mount rename (plan P, P4), on the sample tree: the name, its references and the old one in aliases.</summary>
public sealed class RenameTest : SampleTree
{
	[Fact(DisplayName = "fx mount rename: the name, its references in the space and the old name in aliases; references by it still work, with a warning")]
	public void Rename()
	{
		var acme = Acme();
		var w1 = Path.Combine(acme.Path, "repo-w1");
		// a share of repo-w1 for repo-c1: its space.yaml changes too
		File.AppendAllText(Path.Combine(w1, "_fx", "space.yaml"), "shares:\n  - path: README.md\n    destination: docs/\n    applies-to:\n      repositories: [repo-c1]\n");
		var changes = ManifestEditor.RenameRepository(w1, "repo-c1", "app");
		Assert.All(changes, c => Assert.True(c.Ok, string.Join("; ", c.Diagnostics.Select(d => d.Message.English))));
		Assert.Equal(["_fx/mounts/mounts.yaml", "_fx/space.yaml", "_fx/dotnet/dotnet.yaml"], changes.Select(c => c.File));
		Assert.Contains("  - name: app\n    aliases: [repo-c1]\n", changes[0].After.Text.Replace("\r\n", "\n"));
		Assert.Contains("repositories: [app]", changes[1].After.Text);
		Assert.Contains("repositories: [app]", changes[2].After.Text);
		foreach (var change in changes)
			ManifestEditor.Write(w1, change);

		Assert.Empty(Errors(Sync(acme.Path)));
		var card = FxInherited.Load(Path.Combine(w1, "repo-c1"))!;
		Assert.Equal("/acme/repo-w1/app", card.Space.FullName);
		Assert.Equal(["/acme/repo-w1/repo-c1"], card.Space.Aliases);
		// acme still says repo-w1/repo-c1 nowhere, but the package for app still reaches it
		Assert.Contains("Update=\"Serilog\"", Read(w1, "repo-c1/_fx/dotnet/dotnet.g.targets"));

		// a reference by the old name works, and the doctor says to change it
		var manifest = Path.Combine(w1, "_fx", "mounts", "mounts.yaml");
		// (fx clones with the user's git, which may turn the line endings into CRLF)
		File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\r\n", "\n").Replace("    tags: [dotnet, mobile]", "    depends-on: [repo-c1]\n    tags: [dotnet, mobile]"));
		var doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics).ToList();
		var alias = Assert.Single(doctor, d => d.Code == MountsModule.AliasReference);
		Assert.Contains("'repo-c1' uses 'repo-c1', an old name of 'app'", alias.Message.English);
		Assert.Contains(ManifestEditor.RenameRepository(w1, "app", "repo-c2").Single().Diagnostics, d => d.Code == ManifestEditor.RepositoryExists);
	}

	[Fact(DisplayName = "fx mount rename --output json: every file it changes, with its diff")]
	public void Cli_Rename()
	{
		var acme = Acme();
		var stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["mount", "rename", "repo-c1", "app", "--dry-run", "--output", "json"], stdout, new StringWriter(), Path.Combine(acme.Path, "repo-w1")));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.True(json.GetProperty("changed").GetBoolean());
		Assert.False(json.GetProperty("written").GetBoolean());
		Assert.Equal(["_fx/mounts/mounts.yaml", "_fx/dotnet/dotnet.yaml"], json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("file").GetString()));
		Assert.Contains("+    aliases: [repo-c1]", json.GetProperty("files")[0].GetProperty("diff").GetString());
	}
}
