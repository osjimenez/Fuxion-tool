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

/// <summary>Workspaces inside workspaces (plan P, P4-P6), on the sample tree: inheritance, parents from their remotes, guests, the highest copy, shares.</summary>
public sealed class NestedTest : SampleTree
{
	[Fact(DisplayName = "a tree: each level inherits from its parent, merged; the mandatory repositories of every level are mounted")]
	public void Tree()
	{
		var acme = Acme();
		foreach (var path in new[] { "repo-w1", "libs/shared", "repo-w1/repo-c1", "repo-w1/repo-c2", "repo-w2", "repo-w2/apps/c3" })
			Assert.True(FxLayout.IsGitRoot(Path.Combine(acme.Path, path)), path);

		var c1 = FxInherited.Load(Path.Combine(acme.Path, "repo-w1", "repo-c1"))!;
		Assert.Equal("/acme/repo-w1/repo-c1", c1.Repository.FullName);
		Assert.Equal("/acme/repo-w1", c1.Parent.FullName);
		Assert.Equal(Url(_w1Origin), c1.Parent.Url);
		Assert.Contains(c1.Files, f => f.Path == "dotnet/dotnet.yaml" && f.From.SequenceEqual(["/acme", "/acme/repo-w1"]));
		Assert.Contains(c1.Files, f => f.Path == "dotnet/default.props" && f.From.SequenceEqual(["/acme"]));
		// what acme wrote with a relative name arrives absolute
		Assert.Contains("repositories: [/acme/repo-w1/repo-c1]", Read(acme.Path, "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
		// repo-c1's own variable wins; Serilog is only for it, xunit.v3 for every repo tagged dotnet
		Assert.Contains("<CoreFrameworks>net10.0</CoreFrameworks>", Read(acme.Path, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.props"));
		Assert.Contains("Update=\"Serilog\"", Read(acme.Path, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.targets"));
		Assert.Contains("Update=\"xunit.v3\"", Read(acme.Path, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.targets"));
		Assert.Contains("<CoreFrameworks>net10.0;net11.0</CoreFrameworks>", Read(acme.Path, "repo-w1/repo-c2/_fx/dotnet/dotnet.g.props"));
		Assert.Equal("/acme/shared", FxInherited.Load(Path.Combine(acme.Path, "libs", "shared"))!.Repository.FullName);

		// the solutions from the bottom up; the map of the highest workspace
		Assert.Contains("<Project Path=\"repo-c1/src/App/C1.App.csproj\" />", Read(acme.Path, "repo-w1/W1.slnx"));
		var solution = Read(acme.Path, "Acme.slnx");
		Assert.Contains("<Folder Name=\"/products/w1/C1/src/\">", solution);
		Assert.Contains("<Project Path=\"repo-w1/repo-c2/C2.Lib/C2.Lib.csproj\" />", solution);
		Assert.Contains("<Project Path=\"libs/shared/Shared.csproj\" />", solution);
		var map = Read(acme.Path, "_fx/dotnet/~$workspace.props");
		foreach (var project in new[] { "C1.App", "C2.Lib", "Shared" })
			Assert.Contains($"Include=\"{project}\"", map);
		// libs/ is versioned and shared, inside it, is not; mobile is in repo-w1's catalog
		Assert.True(acme.Git("check-ignore", "libs/shared/").Length > 0);
		var doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics).ToList();
		Assert.DoesNotContain(doctor, d => d.Code is WorkspaceModule.UnknownTag or WorkspaceModule.Outdated or "dotnet.outdated");
	}

	[Fact(DisplayName = "--only: this level alone; what is below stays until a sync without it")]
	public void Only()
	{
		var acme = Acme();
		var yaml = Path.Combine(acme.Path, "_fx", "dotnet", "dotnet.yaml");
		File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("      - version: 4.0.0\n", "      - version: 4.0.0\n  - ids: [Polly]\n    versions:\n      - version: 8.0.0\n"));
		var only = Sync(acme.Path, new(OnlyHere: true));
		Assert.Empty(Errors(only));
		Assert.DoesNotContain("Polly", Read(acme.Path, "repo-w1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.DoesNotContain(only, r => r.Repository is "repo-w1" or "repo-c1");
		Sync(acme.Path);
		Assert.Contains("Polly", Read(acme.Path, "repo-w1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.Contains("Polly", Read(acme.Path, "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
	}

	[Fact(DisplayName = "a clone on its own reads its parent from the remote: its main branch, another with --parent-ref, the cache offline")]
	public void RemoteParent()
	{
		var acme = Acme();
		// what repo-w1 got from acme, versioned, as its team would push it
		var w1 = new TempGitRepository(init: false, path: Path.Combine(acme.Path, "repo-w1"));
		w1.CommitAll("inherited");
		w1.Git("push", "--quiet", "origin", "HEAD");

		WithCache(cache =>
		{
			var loose = Clone(_w1Origin);
			// nothing changed in acme: nothing changes here
			var first = Sync(loose.Path);
			Assert.Empty(Errors(first));
			Assert.All(first.SelectMany(r => r.Files).Where(f => f.Path.StartsWith("_fx/.inherited/", StringComparison.Ordinal)),
				f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
			Assert.True(Directory.Exists(Path.Combine(cache, Path.GetFileName(Sync_Folder(_acmeOrigin)))));

			// a change in acme, pushed: it reaches the clone on its own, and its repositories
			var acmeWork = Clone(_acmeOrigin);
			var yaml = Path.Combine(acmeWork.Path, "_fx", "dotnet", "dotnet.yaml");
			File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("      - version: 4.0.0\n", "      - version: 4.0.0\n  - ids: [Polly]\n    versions:\n      - version: 8.0.0\n"));
			acmeWork.CommitAll("polly");
			acmeWork.Git("push", "--quiet", "origin", "HEAD");
			Assert.Empty(Errors(Sync(loose.Path)));
			Assert.Contains("Polly", Read(loose.Path, "_fx/.inherited/dotnet/dotnet.yaml"));
			Assert.Contains("Polly", Read(loose.Path, "repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
			var card = FxInherited.Load(loose.Path)!;
			Assert.Equal("main", card.Parent.Ref);
			Assert.Equal(acmeWork.Head()[..7], card.Parent.Commit);

			// another branch of acme, to try it without versioning the choice
			acmeWork.Branch("feature/x");
			File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("value: net10.0;net11.0", "value: net11.0"));
			acmeWork.CommitAll("net11 only");
			acmeWork.Git("push", "--quiet", "origin", "feature/x");
			Assert.Empty(Errors(Sync(loose.Path, new(ParentRef: "feature/x"))));
			Assert.Contains("value: net11.0\n", Read(loose.Path, "_fx/.inherited/dotnet/dotnet.yaml"));
			Assert.Equal("feature/x", FxInherited.Load(loose.Path)!.Parent.Ref);
			Sync(loose.Path);
			Assert.Contains("value: net10.0;net11.0", Read(loose.Path, "_fx/.inherited/dotnet/dotnet.yaml"));

			// offline: the cache as it is
			Assert.Empty(Errors(Sync(loose.Path, new(Offline: true))));
			return 0;
		});

		// offline and without a cache: what it inherits stays as it is, and it says why
		WithCache(_ =>
		{
			var loose = Clone(_w1Origin);
			var results = Sync(loose.Path, new(Offline: true));
			Assert.Contains(results.SelectMany(r => r.Diagnostics), d => d.Code == Core.Sync.RemoteParent.Unavailable);
			Assert.DoesNotContain(results.SelectMany(r => r.Files), f => f.Path.StartsWith("_fx/.inherited/", StringComparison.Ordinal) && f.Status != SyncFileStatus.Unchanged);
			return 0;
		});
	}

	static string Sync_Folder(TempGitRepository origin) => Core.Sync.RemoteParent.Folder(Url(origin));

	[Fact(DisplayName = "fx repo rename: the name, its references in the workspace and the old name in aliases; references by it still work, with a warning")]
	public void Rename()
	{
		var acme = Acme();
		var w1 = Path.Combine(acme.Path, "repo-w1");
		var changes = ManifestEditor.RenameRepository(w1, "repo-c1", "app");
		Assert.All(changes, c => Assert.True(c.Ok, string.Join("; ", c.Diagnostics.Select(d => d.Message.English))));
		Assert.Equal(["_fx/workspace/workspace.yaml", "_fx/dotnet/dotnet.yaml"], changes.Select(c => c.File));
		Assert.Contains("  - name: app\n    aliases: [repo-c1]\n", changes[0].After.Text.Replace("\r\n", "\n"));
		Assert.Contains("repositories: [app]", changes[1].After.Text);
		foreach (var change in changes)
			ManifestEditor.Write(w1, change);

		Assert.Empty(Errors(Sync(acme.Path)));
		var card = FxInherited.Load(Path.Combine(w1, "repo-c1"))!;
		Assert.Equal("/acme/repo-w1/app", card.Repository.FullName);
		Assert.Equal(["/acme/repo-w1/repo-c1"], card.Repository.Aliases);
		// acme still says repo-w1/repo-c1 nowhere, but the package for app still reaches it
		Assert.Contains("Update=\"Serilog\"", Read(w1, "repo-c1/_fx/dotnet/dotnet.g.targets"));

		// a reference by the old name works, and the doctor says to change it
		var manifest = Path.Combine(w1, "_fx", "workspace", "workspace.yaml");
		// (fx clones with the user's git, which may turn the line endings into CRLF)
		File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\r\n", "\n").Replace("    tags: [dotnet, mobile]\n", "    depends-on: [repo-c1]\n    tags: [dotnet, mobile]\n"));
		var doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics).ToList();
		var alias = Assert.Single(doctor, d => d.Code == WorkspaceModule.AliasReference);
		Assert.Contains("'repo-c1' uses 'repo-c1', an old name of 'app'", alias.Message.English);
		Assert.Contains(ManifestEditor.RenameRepository(w1, "app", "repo-c2").Single().Diagnostics, d => d.Code == ManifestEditor.RepositoryExists);
	}

	[Fact(DisplayName = "fx repo rename --output json: every file it changes, with its diff")]
	public void Cli_Rename()
	{
		var acme = Acme();
		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "rename", "repo-c1", "app", "--dry-run", "--output", "json"], stdout, new StringWriter(), Path.Combine(acme.Path, "repo-w1")));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.True(json.GetProperty("changed").GetBoolean());
		Assert.False(json.GetProperty("written").GetBoolean());
		Assert.Equal(["_fx/workspace/workspace.yaml", "_fx/dotnet/dotnet.yaml"], json.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("file").GetString()));
		Assert.Contains("+    aliases: [repo-c1]", json.GetProperty("files")[0].GetProperty("diff").GetString());
	}

	[Fact(DisplayName = "a guest on its own tree: used (its solution, the map), but nothing reaches it from the workspace that mounts it")]
	public void Guest_Alone()
	{
		var acme = Acme();
		// what repo-w2 got from acme, versioned, as its team would push it
		var w2 = new TempGitRepository(init: false, path: Path.Combine(acme.Path, "repo-w2"));
		w2.CommitAll("inherited");
		w2.Git("push", "--quiet", "origin", "HEAD");

		WithCache(_ =>
		{
			var loose = Clone(_w2Origin);
			Assert.Empty(Errors(Sync(loose.Path)));
			Assert.True(FxLayout.IsGitRoot(Path.Combine(loose.Path, "libs", "shared")));
			Assert.Contains("<Project Path=\"libs/shared/Shared.csproj\" />", Read(loose.Path, "W2.slnx"));
			Assert.Contains(Project(loose.Path, "libs", "shared", "Shared.csproj"), Read(loose.Path, "_fx/dotnet/~$workspace.props"));
			Assert.False(Directory.Exists(Path.Combine(loose.Path, "libs", "shared", "_fx")));
			// c3 is its own: it inherits through repo-w2
			Assert.Equal("/acme/repo-w2/c3", FxInherited.Load(Path.Combine(loose.Path, "apps", "c3"))!.Repository.FullName);
			return 0;
		});
	}

	[Fact(DisplayName = "the highest copy is used: a lower one is not cloned, or is parked; it comes back when the higher one goes; two at the same height are an error")]
	public void Guest_HighestCopy()
	{
		var acme = Acme();
		var w2 = Path.Combine(acme.Path, "repo-w2");
		var map = () => Read(acme.Path, "_fx/dotnet/~$workspace.props");

		// from acme, two copies of shared: acme's is the highest, so repo-w2's is never cloned
		Assert.True(FxLayout.IsGitRoot(Path.Combine(w2, "apps", "c3")));
		Assert.False(Directory.Exists(Path.Combine(w2, "libs", "shared")));
		var superseded = Assert.Single(Sync(acme.Path).SelectMany(r => r.Actions), a => a.Action == "superseded");
		Assert.Equal("shared", superseded.Repository);
		Assert.Equal("the copy of /acme/shared is used", superseded.Detail!.English);
		Assert.Contains(Project(acme.Path, "libs", "shared", "Shared.csproj"), map());
		Assert.Contains(Project(w2, "apps", "c3", "C3.Api", "C3.Api.csproj"), map());

		// a clone of it put there by hand: the doctor says it is not used, and sync parks it
		Clone(_sharedOrigin, Path.Combine(w2, "libs", "shared"));
		var notParked = Assert.Single(SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics),
			d => d.Code == WorkspaceModule.NotParked);
		Assert.Equal("'shared' at libs/shared is not used: the copy of /acme/shared is higher. fx sync parks this one.", notParked.Message.English);
		Assert.Contains(Sync(acme.Path).SelectMany(r => r.Actions), a => a is { Repository: "shared", Action: "parked" });
		Assert.True(FxLayout.IsGitRoot(Path.Combine(w2, "_fx", "~$parking", "libs", "shared")));
		Assert.Contains(Project(acme.Path, "libs", "shared", "Shared.csproj"), map());

		// acme stops mounting its copy: repo-w2's is the highest now, and comes back, still a guest
		var manifest = Path.Combine(acme.Path, "_fx", "workspace", "workspace.yaml");
		File.WriteAllText(manifest, Read(acme.Path, "_fx/workspace/workspace.yaml").Replace("    path: libs/shared\n    mount: mandatory\n", "    path: libs/shared\n    mount: manual\n"));
		Assert.Empty(WorkspaceModule.SetMount(WorkspaceManifest.Load(acme.Path), ["shared"], mounted: false).Diagnostics);
		var back = Sync(acme.Path);
		Assert.Empty(Errors(back));
		Assert.Contains(back.SelectMany(r => r.Actions), a => a is { Repository: "shared", Action: "unparked" });
		Assert.True(FxLayout.IsGitRoot(Path.Combine(w2, "libs", "shared")));
		Assert.Contains(Project(w2, "libs", "shared", "Shared.csproj"), map());
		Assert.False(Directory.Exists(Path.Combine(w2, "libs", "shared", "_fx")));

		// repo-w1 mounts it as a guest too: two copies at the same height, an error, and nothing moves
		var w1Manifest = Path.Combine(acme.Path, "repo-w1", "_fx", "workspace", "workspace.yaml");
		File.WriteAllText(w1Manifest, Read(acme.Path, "repo-w1/_fx/workspace/workspace.yaml")
			.Replace("paths:\n", $"  - name: shared\n    url: {Url(_sharedOrigin)}\n    path: libs/shared\n    mount: mandatory\n    inherits: false\n    tags: [dotnet]\npaths:\n"));
		var tie = Assert.Single(Errors(Sync(acme.Path)));
		Assert.Equal(WorkspaceCopies.SameHeight, tie.Code);
		Assert.Equal("'shared' is mounted twice at the same height, as /acme/repo-w1/shared and /acme/repo-w2/shared: fx cannot tell which copy to use, and parks neither.",
			tie.Message.English);
		Assert.Equal("repo-w1/_fx/workspace/workspace.yaml", tie.File);
		Assert.True(FxLayout.IsGitRoot(Path.Combine(w2, "libs", "shared")));
		Assert.True(FxLayout.IsGitRoot(Path.Combine(acme.Path, "repo-w1", "libs", "shared")));
	}

	static readonly byte[] Icon = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, 3];

	/// <summary>acme shares, pushed: its icon for the libraries, its LICENSE for every repository. Returns its work clone.</summary>
	TempGitRepository ShareFromAcme()
	{
		var work = Clone(_acmeOrigin);
		Directory.CreateDirectory(Path.Combine(work.Path, "res"));
		File.WriteAllBytes(Path.Combine(work.Path, "res", "icon.png"), Icon);
		work.WriteFile("LICENSE", "MIT acme\n");
		var manifest = Path.Combine(work.Path, "_fx", "workspace", "workspace.yaml");
		// (the manifest of the fixture does not end in a new line)
		File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\r\n", "\n").Replace("paths:\n", "paths:\n  - path: res/\n  - path: LICENSE\n").TrimEnd('\n')
			+ "\nshares:\n  - path: res/icon.png\n    applies-to:\n      tags: [library]\n  - path: LICENSE\n");
		work.CommitAll("shares");
		work.Git("push", "--quiet", "origin", "HEAD");
		return work;
	}

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
		Assert.Contains("  - path: res/icon.png\n    destination: res/icon.png\n    applies-to:\n      tags: [library]\n", Read(w1, "_fx/.inherited/workspace/workspace.yaml"));
		Assert.Equal(Icon, File.ReadAllBytes(Path.Combine(w1, "_fx", ".inherited", "workspace", "res", "icon.png")));
		Assert.Equal("MIT acme\n", Read(w1, "repo-c1/LICENSE"));
		Assert.False(Directory.Exists(Path.Combine(w1, "repo-c1", "res")));
		Assert.DoesNotContain(SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics),
			d => d.Severity == FxSeverity.Error);

		// repo-w1 shares its own LICENSE: the nearest wins, below it
		File.WriteAllText(Path.Combine(w1, "LICENSE.w1"), "MIT w1\n");
		var w1Manifest = Path.Combine(w1, "_fx", "workspace", "workspace.yaml");
		Append(w1Manifest, "paths:\n", "paths:\n  - path: LICENSE.w1\n");
		File.AppendAllText(w1Manifest, "\nshares:\n  - path: LICENSE.w1\n    destination: LICENSE\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.Equal("MIT w1\n", Read(w1, "repo-c1/LICENSE"));
		Assert.Equal("/acme/repo-w1", FxInherited.Load(Path.Combine(w1, "repo-c1"))!.Shares.Single(s => s.Destination == "LICENSE").From);
		Assert.Equal("MIT acme\n", Read(w1, "LICENSE"));
		Assert.Equal("MIT acme\n", Read(shared, "LICENSE"));

		// a file of the repository's own at a destination: not overwritten, and it says so; --adopt takes it
		var c2Notice = Path.Combine(w1, "repo-c2", "NOTICE");
		File.WriteAllText(c2Notice, "c2's own\n");
		File.WriteAllText(Path.Combine(acme.Path, "NOTICE"), "acme notice\n");
		var acmeManifest = Path.Combine(acme.Path, "_fx", "workspace", "workspace.yaml");
		Append(acmeManifest, "paths:\n", "paths:\n  - path: NOTICE\n");
		File.AppendAllText(acmeManifest, "  - path: NOTICE\n");
		var refused = Assert.Single(Errors(Sync(acme.Path)));
		Assert.Equal(WorkspaceShares.Refused, refused.Code);
		Assert.Equal("repo-w1/repo-c2/NOTICE is the repository's own: fx does not overwrite it with the share of /acme.", refused.Message.English);
		Assert.Equal("c2's own\n", File.ReadAllText(c2Notice));
		Assert.Equal("acme notice\n", Read(shared, "NOTICE"));
		Assert.Empty(Errors(Sync(acme.Path, new(Adopt: true))));
		Assert.Equal("acme notice\n", Read(w1, "repo-c2/NOTICE"));
		Assert.Contains(FxInherited.Load(Path.Combine(w1, "repo-c2"))!.Shares, s => s.Destination == "NOTICE");

		// a share that goes: what fx gave goes too, and the folders it leaves empty
		Append(acmeManifest, "  - path: res/icon.png\n    applies-to:\n      tags: [library]\n", "");
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.False(Directory.Exists(Path.Combine(shared, "res")));
		Assert.False(Directory.Exists(Path.Combine(w1, "_fx", ".inherited", "workspace", "res")));
		Assert.DoesNotContain(FxInherited.Load(shared)!.Shares, s => s.Destination == "res/icon.png");

		// the doctor of the workspace that shares: a source it does not version, and one that is not there
		File.WriteAllText(Path.Combine(acme.Path, "notes.txt"), "notes\n");
		File.AppendAllText(acmeManifest, "  - path: notes.txt\n  - path: missing.txt\n");
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
		var manifest = Path.Combine(acme.Path, "_fx", "workspace", "workspace.yaml");
		Append(manifest, "paths:\n", "paths:\n  - path: cfg/\n");
		File.AppendAllText(manifest, "\nshares:\n  - path: cfg/\n    destination: build\n    applies-to:\n      tags: [app]\n"
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
		var stored = Read(w1, "_fx/.inherited/workspace/workspace.yaml");
		Assert.Contains("  - path: build/\n    destination: build/\n", stored);
		Assert.Contains("repositories: [/acme/repo-w1/repo-c1]", stored);
		Assert.Equal("b\n", Read(w1, "_fx/.inherited/workspace/build/sub/b.txt"));

		// a file of the folder goes: it goes from the repositories too
		File.Delete(Path.Combine(acme.Path, "cfg", "sub", "b.txt"));
		Assert.Empty(Errors(Sync(acme.Path)));
		Assert.False(Directory.Exists(Path.Combine(c1, "build", "sub")));
		Assert.True(File.Exists(Path.Combine(c1, "build", "a.txt")));
	}

	[Fact(DisplayName = "shares from a parent read from its remote: its sources come with it, in the cache")]
	public void Shares_RemoteParent()
	{
		var work = ShareFromAcme();
		var acme = Acme();
		// what shared got from acme, versioned, as its team would push it
		var shared = new TempGitRepository(init: false, path: Path.Combine(acme.Path, "libs", "shared"));
		shared.CommitAll("inherited");
		shared.Git("push", "--quiet", "origin", "HEAD");

		// a new icon in acme, pushed: it reaches a clone of shared on its own
		byte[] icon = [.. Icon, 4, 5];
		File.WriteAllBytes(Path.Combine(work.Path, "res", "icon.png"), icon);
		work.CommitAll("new icon");
		work.Git("push", "--quiet", "origin", "HEAD");
		WithCache(cache =>
		{
			var loose = Clone(_sharedOrigin);
			Assert.Equal(Icon, File.ReadAllBytes(Path.Combine(loose.Path, "res", "icon.png")));
			Assert.Empty(Errors(Sync(loose.Path)));
			Assert.Equal(icon, File.ReadAllBytes(Path.Combine(loose.Path, "res", "icon.png")));
			Assert.True(File.Exists(Path.Combine(Sync_Folder(_acmeOrigin), "res", "icon.png")));
			return 0;
		});
	}
}
