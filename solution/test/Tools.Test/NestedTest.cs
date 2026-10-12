using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>
/// Workspaces inside workspaces (plan P, P4 and P5), on the sample tree of the plan: acme, the root, mounts the
/// products repo-w1 (a workspace with repo-c1 and repo-c2) and repo-w2 (with c3, and shared as a guest), and the
/// library shared. Every repository has a bare origin, so a clone of a product on its own reads acme from its remote.
/// </summary>
public sealed class NestedTest : IDisposable
{
	readonly List<TempGitRepository> _repos = [];

	/// <summary>A repository written from <paramref name="files"/> (path and content) and committed, and its bare origin; returns the origin.</summary>
	TempGitRepository Origin(params (string Path, string Content)[] files)
	{
		var source = new TempGitRepository();
		_repos.Add(source);
		// LF, whatever the line endings of this file in the checkout (its raw literals take them)
		foreach (var (path, content) in files)
			source.WriteFile(path, content.Replace("\r\n", "\n"));
		source.CommitAll("c1");
		return Bare(source);
	}

	TempGitRepository Bare(TempGitRepository source)
	{
		var bare = new TempGitRepository(init: false);
		_repos.Add(bare);
		source.Git("clone", "--quiet", "--bare", source.Path, bare.Path);
		return bare;
	}

	/// <summary>A working clone of <paramref name="origin"/>, where commits can be made and pushed.</summary>
	TempGitRepository Clone(TempGitRepository origin, string? path = null)
	{
		var clone = new TempGitRepository(init: false, path: path);
		_repos.Add(clone);
		clone.Git("clone", "--quiet", origin.Path, clone.Path);
		return clone;
	}

	static string Url(TempGitRepository repo) => repo.Path.Replace('\\', '/');

	readonly TempGitRepository _acmeOrigin, _w1Origin, _w2Origin, _sharedOrigin;

	public NestedTest()
	{
		var c1 = Origin(
			(".gitignore", "~$*\n"),
			("C1.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/App/C1.App.csproj\" />\n  </Folder>\n</Solution>\n"),
			("src/App/C1.App.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" /><PackageReference Include=\"Serilog\" /></ItemGroup></Project>\n"),
			("_fx/dotnet/dotnet.yaml", "schema: 1.0.0\nvariables:\n  - name: CoreFrameworks\n    value: net10.0\n"));
		var c2 = Origin(
			(".gitignore", "~$*\n"),
			("C2.slnx", "<Solution>\n  <Project Path=\"C2.Lib/C2.Lib.csproj\" />\n</Solution>\n"),
			("C2.Lib/C2.Lib.csproj", "<Project />\n"));
		var shared = _sharedOrigin = Origin(
			(".gitignore", "~$*\n"),
			("Shared.slnx", "<Solution>\n  <Project Path=\"Shared.csproj\" />\n</Solution>\n"),
			("Shared.csproj", "<Project />\n"));
		_w1Origin = Origin(
			("README.md", "repo-w1\n"),
			("_fx/fx.yaml", "schema: 1.0.0\ntags:\n  - name: mobile\n    description: The mobile part.\n"),
			("_fx/workspace/workspace.yaml", $"""
				schema: 1.0.0
				repositories:
				  - name: repo-c1
				    url: {Url(c1)}
				    path: repo-c1
				    mount: mandatory
				    tags: [dotnet, app]
				  - name: repo-c2
				    url: {Url(c2)}
				    path: repo-c2
				    mount: mandatory
				    tags: [dotnet, mobile]
				paths:
				  - path: README.md
				"""),
			("_fx/dotnet/dotnet.yaml", """
				schema: 1.0.0
				packages:
				  - ids: [Serilog]
				    versions:
				      - version: 4.0.0
				    applies-to:
				      repositories: [repo-c1]
				solutions:
				  - path: W1.slnx
				    folders:
				      - name: /C1
				        solutions: [repo-c1/C1.slnx]
				      - name: /C2
				        solutions: [repo-c2/C2.slnx]
				"""));
		var c3 = Origin(
			(".gitignore", "~$*\n"),
			("C3.slnx", "<Solution>\n  <Project Path=\"C3.Api/C3.Api.csproj\" />\n</Solution>\n"),
			("C3.Api/C3.Api.csproj", "<Project />\n"));
		// shared, also here as a guest: used, but it inherits from acme
		_w2Origin = Origin(
			("README.md", "repo-w2\n"),
			("_fx/workspace/workspace.yaml", $"""
				schema: 1.0.0
				repositories:
				  - name: c3
				    url: {Url(c3)}
				    path: apps/c3
				    mount: mandatory
				    tags: [dotnet, app]
				  - name: shared
				    url: {Url(shared)}
				    path: libs/shared
				    mount: mandatory
				    inherits: false
				    tags: [dotnet]
				paths:
				  - path: README.md
				"""),
			("_fx/dotnet/dotnet.yaml", """
				schema: 1.0.0
				solutions:
				  - path: W2.slnx
				    folders:
				      - name: /C3
				        solutions: [apps/c3/C3.slnx]
				      - name: /shared
				        solutions: [libs/shared/Shared.slnx]
				"""));
		_acmeOrigin = Origin(
			("README.md", "acme\n"),
			("libs/README.md", "libraries\n"),
			("_fx/fx.yaml", "schema: 1.0.0\nname: acme\ntags:\n  - name: dotnet\n  - name: product\n  - name: library\n  - name: app\n"),
			("_fx/workspace/workspace.yaml", $"""
				schema: 1.0.0
				repositories:
				  - name: repo-w1
				    url: {Url(_w1Origin)}
				    path: repo-w1
				    mount: mandatory
				    tags: [dotnet, product]
				  - name: repo-w2
				    url: {Url(_w2Origin)}
				    path: repo-w2
				    mount: mandatory
				    tags: [dotnet, product]
				  - name: shared
				    url: {Url(shared)}
				    path: libs/shared
				    mount: mandatory
				    tags: [dotnet, library]
				paths:
				  - path: README.md
				  - path: libs/
				"""),
			("_fx/dotnet/default.props", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>\n"),
			("_fx/dotnet/dotnet.yaml", """
				schema: 1.0.0
				applies-to:
				  tags: [dotnet]
				variables:
				  - name: CoreFrameworks
				    value: net10.0;net11.0
				imports:
				  - props: default.props
				packages:
				  - ids: [xunit.v3]
				    versions:
				      - version: 4.0.0
				solutions:
				  - path: Acme.slnx
				    folders:
				      - name: /libs/shared
				        solutions: [libs/shared/Shared.slnx]
				      - name: /products/w1
				        solutions: [repo-w1/W1.slnx]
				      - name: /products/w2
				        solutions: [repo-w2/W2.slnx]
				"""));
	}

	public void Dispose()
	{
		foreach (var repo in Enumerable.Reverse(_repos))
			repo.Dispose();
	}

	static IReadOnlyList<FxModuleResult> Sync(string directory, SyncOptions? options = null)
		=> SyncEngine.Sync(directory, options ?? new(), cancellationToken: TestContext.Current.CancellationToken);

	static IEnumerable<FxDiagnostic> Errors(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Diagnostics).Where(d => d.Severity == FxSeverity.Error);

	static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative)).Replace("\r\n", "\n");

	/// <summary>acme cloned and synced: every mandatory repository, at every level.</summary>
	TempGitRepository Acme()
	{
		var acme = Clone(_acmeOrigin);
		Assert.Empty(Errors(Sync(acme.Path)));
		return acme;
	}

	/// <summary>With the cache of the parents read from their remotes in a folder of its own.</summary>
	static T WithCache<T>(Func<string, T> body)
	{
		var cache = Directory.CreateTempSubdirectory("fx-cache-").FullName;
		var before = Environment.GetEnvironmentVariable("FX_CACHE");
		Environment.SetEnvironmentVariable("FX_CACHE", cache);
		try
		{
			return body(cache);
		}
		finally
		{
			Environment.SetEnvironmentVariable("FX_CACHE", before);
			new TempGitRepository(init: false, path: cache).Dispose();
		}
	}

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

	static string Project(params string[] path) => $"Path=\"{Path.GetFullPath(Path.Combine(path))}\"";

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
}
