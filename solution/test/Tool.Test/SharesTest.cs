using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Workspace;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The shares (plan P, P6), on the sample tree: down through the workspaces in between, folders and files.</summary>
public sealed class SharesTest : SampleTree
{
	[Fact(DisplayName = "shares: down through the workspaces in between, to the repositories they apply to; the nearest wins; a file of the repository's own is not overwritten")]
	public void Shares()
	{
		ShareFromAcme();
		var acme = Acme();
		var shared = Path.Combine(acme.Path, "libs", "shared");
		var w1 = Path.Combine(acme.Path, "repo-w1");

		// shared, a library: the icon and the LICENSE, versioned in it, and fx's because its inherited.yaml says so
		Assert.Equal(Icon, File.ReadAllBytes(Path.Combine(shared, "res", "icon.png")));
		Assert.Equal("MIT acme\n", Read(shared, "LICENSE"));
		Assert.Equal([("LICENSE", "/acme"), ("res/icon.png", "/acme")], FxInherited.Load(shared)!.Shares.Select(s => (s.Destination, s.From)));
		// repo-w1, in between: only the LICENSE for itself (versioned by its .gitignore), all of it to pass on
		Assert.Equal("MIT acme\n", Read(w1, "LICENSE"));
		Assert.False(File.Exists(Path.Combine(w1, "res", "icon.png")));
		Assert.False(GitClient.Discover(w1)!.Ignores("LICENSE"));
		Assert.Contains("  - path: res/icon.png\n    destination: res/icon.png\n    applies-to:\n      tags: [library]\n", Read(w1, "_fx/.inherited/shares/shares.yaml"));
		Assert.Equal(Icon, File.ReadAllBytes(Path.Combine(w1, "_fx", ".inherited", "shares", "res", "icon.png")));
		Assert.Equal("MIT acme\n", Read(w1, "repo-c1/LICENSE"));
		Assert.False(Directory.Exists(Path.Combine(w1, "repo-c1", "res")));
		Assert.DoesNotContain(SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics),
			d => d.Severity == FxSeverity.Error);

		// repo-w1 shares its own LICENSE: the nearest wins, below it
		File.WriteAllText(Path.Combine(w1, "LICENSE.w1"), "MIT w1\n");
		var w1Space = Path.Combine(w1, "_fx", "space.yaml");
		Append(w1Space, "paths:\n", "paths:\n  - path: LICENSE.w1\n");
		File.AppendAllText(w1Space, "\nshares:\n  - path: LICENSE.w1\n    destination: LICENSE\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.Equal("MIT w1\n", Read(w1, "repo-c1/LICENSE"));
		Assert.Equal("/acme/repo-w1", FxInherited.Load(Path.Combine(w1, "repo-c1"))!.Shares.Single(s => s.Destination == "LICENSE").From);
		Assert.Equal("MIT acme\n", Read(w1, "LICENSE"));
		Assert.Equal("MIT acme\n", Read(shared, "LICENSE"));

		// a file of the repository's own at a destination: not overwritten, and it says so; --adopt takes it
		var c2Notice = Path.Combine(w1, "repo-c2", "NOTICE");
		File.WriteAllText(c2Notice, "c2's own\n");
		File.WriteAllText(Path.Combine(acme.Path, "NOTICE"), "acme notice\n");
		var acmeSpace = Path.Combine(acme.Path, "_fx", "space.yaml");
		Append(acmeSpace, "paths:\n", "paths:\n  - path: NOTICE\n");
		File.AppendAllText(acmeSpace, "  - path: NOTICE\n");
		var refused = Assert.Single(Errors(Sync(acme.Path)));
		Assert.Equal(WorkspaceShares.Refused, refused.Code);
		Assert.Equal("repo-w1/repo-c2/NOTICE is the repository's own: fx does not overwrite it with the share of /acme.", refused.Message.English);
		Assert.Equal("c2's own\n", File.ReadAllText(c2Notice));
		Assert.Equal("acme notice\n", Read(shared, "NOTICE"));
		Assert.Empty(Errors(Sync(acme.Path, new(Adopt: true))));
		Assert.Equal("acme notice\n", Read(w1, "repo-c2/NOTICE"));
		Assert.Contains(FxInherited.Load(Path.Combine(w1, "repo-c2"))!.Shares, s => s.Destination == "NOTICE");

		// a share that goes: what fx gave goes too, and the folders it leaves empty
		Append(acmeSpace, "  - path: res/icon.png\n    applies-to:\n      tags: [library]\n", "");
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.False(Directory.Exists(Path.Combine(shared, "res")));
		Assert.False(Directory.Exists(Path.Combine(w1, "_fx", ".inherited", "shares", "res")));
		Assert.DoesNotContain(FxInherited.Load(shared)!.Shares, s => s.Destination == "res/icon.png");

		// the doctor of the workspace that shares: a source it does not version, and one that is not there
		File.WriteAllText(Path.Combine(acme.Path, "notes.txt"), "notes\n");
		File.AppendAllText(acmeSpace, "  - path: notes.txt\n  - path: missing.txt\n");
		var doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics).ToList();
		var notVersioned = Assert.Single(doctor, d => d.Code == WorkspaceShares.NotVersioned);
		Assert.Equal("fx path add notes.txt", notVersioned.Fix!.Command);
		Assert.Equal("'missing.txt' is shared, but it is not there: nothing of it goes down, and the repositories keep what they have of it.",
			Assert.Single(doctor, d => d.Code == WorkspaceShares.Missing).Message.English);
	}

	[Fact(DisplayName = "shares: a folder shares its content, a file to a folder keeps its name, references by name are absolute on their way down")]
	public void Shares_Folder()
	{
		var acme = Acme();
		Directory.CreateDirectory(Path.Combine(acme.Path, "cfg", "sub"));
		File.WriteAllText(Path.Combine(acme.Path, "cfg", "a.txt"), "a\n");
		File.WriteAllText(Path.Combine(acme.Path, "cfg", "sub", "b.txt"), "b\n");
		var space = Path.Combine(acme.Path, "_fx", "space.yaml");
		Append(space, "paths:\n", "paths:\n  - path: cfg/\n");
		File.AppendAllText(space, "\nshares:\n  - path: cfg/\n    destination: build\n    applies-to:\n      tags: [app]\n"
		                             + "  - path: README.md\n    destination: docs/\n    applies-to:\n      repositories: [repo-w1/repo-c1]\n");
		Assert.Empty(Errors(Sync(acme.Path)));

		var w1 = Path.Combine(acme.Path, "repo-w1");
		var c1 = Path.Combine(w1, "repo-c1");
		Assert.Equal("a\n", Read(c1, "build/a.txt"));
		Assert.Equal("b\n", Read(c1, "build/sub/b.txt"));
		Assert.Equal("acme\n", Read(c1, "docs/README.md"));
		Assert.Equal("b\n", Read(acme.Path, "repo-w2/apps/c3/build/sub/b.txt"));
		Assert.False(Directory.Exists(Path.Combine(w1, "repo-c2", "build")));
		Assert.False(Directory.Exists(Path.Combine(w1, "repo-c2", "docs")));
		var stored = Read(w1, "_fx/.inherited/shares/shares.yaml");
		Assert.Contains("  - path: build/\n    destination: build/\n", stored);
		Assert.Contains("repositories: [/acme/repo-w1/repo-c1]", stored);
		Assert.Equal("b\n", Read(w1, "_fx/.inherited/shares/build/sub/b.txt"));

		// a file of the folder goes: it goes from the repositories too
		File.Delete(Path.Combine(acme.Path, "cfg", "sub", "b.txt"));
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.False(Directory.Exists(Path.Combine(c1, "build", "sub")));
		Assert.True(File.Exists(Path.Combine(c1, "build", "a.txt")));
	}
}
