using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Cli.Describe;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The sample tree of plan P, with real git repositories under the temp folder: acme, the root, mounts the products
/// repo-w1 (a workspace with repo-c1 and repo-c2) and repo-w2 (with c3, and shared as a guest), and the library shared.
/// Every repository has a bare origin, so a clone of a product on its own reads acme from its remote.
/// </summary>
public abstract class SampleTree : IDisposable
{
	readonly List<TempGitRepository> _repos = [];

	/// <summary>A repository written from <paramref name="files"/> (path and content) and committed, and its bare origin; returns the origin.</summary>
	protected TempGitRepository Origin(params (string Path, string Content)[] files)
	{
		var source = new TempGitRepository();
		_repos.Add(source);
		// LF, whatever the line endings of this file in the checkout (its raw literals take them)
		foreach (var (path, content) in files)
			source.WriteFile(path, content.Replace("\r\n", "\n"));
		source.CommitAll("c1");
		return Bare(source);
	}

	protected TempGitRepository Bare(TempGitRepository source)
	{
		var bare = new TempGitRepository(init: false);
		_repos.Add(bare);
		source.Git("clone", "--quiet", "--bare", source.Path, bare.Path);
		return bare;
	}

	/// <summary>A working clone of <paramref name="origin"/>, where commits can be made and pushed.</summary>
	protected TempGitRepository Clone(TempGitRepository origin, string? path = null)
	{
		var clone = new TempGitRepository(init: false, path: path);
		_repos.Add(clone);
		clone.Git("clone", "--quiet", origin.Path, clone.Path);
		return clone;
	}

	protected static string Url(TempGitRepository repo) => repo.Path.Replace('\\', '/');

	protected readonly TempGitRepository _acmeOrigin, _w1Origin, _w2Origin, _sharedOrigin;

	protected SampleTree()
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
			("_fx/space.yaml", "schema: 1.0.0\ntags:\n  - name: mobile\n    description: The mobile part.\npaths:\n  - path: README.md\n"),
			("_fx/mounts/mounts.yaml", $"""
				schema: 1.0.0
				mounts:
				  - name: repo-c1
				    url: {Url(c1)}
				    path: repo-c1
				    required: true
				    tags: [dotnet, app]
				  - name: repo-c2
				    url: {Url(c2)}
				    path: repo-c2
				    required: true
				    tags: [dotnet, mobile]
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
			("_fx/space.yaml", "schema: 1.0.0\npaths:\n  - path: README.md\n"),
			("_fx/mounts/mounts.yaml", $"""
				schema: 1.0.0
				mounts:
				  - name: c3
				    url: {Url(c3)}
				    path: apps/c3
				    required: true
				    tags: [dotnet, app]
				  - name: shared
				    url: {Url(shared)}
				    path: libs/shared
				    required: true
				    inherits: false
				    tags: [dotnet]
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
			("_docs/guide.md", "guide\n"),
			("_fx/space.yaml", """
				schema: 1.0.0
				name: acme
				tags:
				  - name: dotnet
				  - name: product
				  - name: library
				  - name: app
				paths:
				  - path: README.md
				  - path: libs/
				  - path: _docs/
				    worktree: false
				"""),
			("_fx/mounts/mounts.yaml", $"""
				schema: 1.0.0
				mounts:
				  - name: repo-w1
				    url: {Url(_w1Origin)}
				    path: repo-w1
				    required: true
				    tags: [dotnet, product]
				  - name: repo-w2
				    url: {Url(_w2Origin)}
				    path: repo-w2
				    required: true
				    tags: [dotnet, product]
				  - name: shared
				    url: {Url(shared)}
				    path: libs/shared
				    required: true
				    tags: [dotnet, library]
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
		GC.SuppressFinalize(this);
		foreach (var repo in Enumerable.Reverse(_repos))
			repo.Dispose();
	}

	protected static IReadOnlyList<FxModuleResult> Sync(string directory, SyncOptions? options = null)
		=> SyncEngine.Sync(directory, (options ?? new()) with { Skill = FxSkill.Text }, cancellationToken: TestContext.Current.CancellationToken);

	protected static IEnumerable<FxDiagnostic> Errors(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Diagnostics).Where(d => d.Severity == FxSeverity.Error);

	protected static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative)).Replace("\r\n", "\n");

	/// <summary>acme cloned and synced: every mandatory repository, at every level.</summary>
	protected TempGitRepository Acme()
	{
		var acme = Clone(_acmeOrigin);
		Assert.Empty(Errors(Sync(acme.Path)));
		return acme;
	}

	/// <summary>With the cache of the parents read from their remotes in a folder of its own.</summary>
	protected static T WithCache<T>(Func<string, T> body)
	{
		var cache = TempGitRepository.TempFolder("fx-cache-");
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

	/// <summary>The repositories of the tree once acme is synced, relative to it ('.', acme itself).</summary>
	protected static readonly string[] Repositories = [".", "repo-w1", "repo-w1/repo-c1", "repo-w1/repo-c2", "repo-w2", "repo-w2/apps/c3", "libs/shared"];

	/// <summary>acme synced, and what sync wrote committed in every repository, as each team would have it.</summary>
	protected TempGitRepository AcmeCommitted()
	{
		var acme = Acme();
		foreach (var path in Repositories)
		{
			var full = Path.Combine(acme.Path, path);
			acme.Git("-C", full, "add", "-A");
			acme.Git("-C", full, "commit", "--quiet", "--allow-empty", "-m", "synced");
		}
		return acme;
	}

	/// <summary>A project as the map of FxReference has it: its full path.</summary>
	protected static string Project(params string[] path) => $"Path=\"{Path.GetFullPath(Path.Combine(path))}\"";

	protected static void Append(string file, string from, string to) => File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace(from, to));

	/// <summary>The folder of the cache where fx keeps a parent read from <paramref name="origin"/>.</summary>
	protected static string CacheFolder(TempGitRepository origin) => Core.Sync.RemoteParent.Folder(Url(origin));

	protected static readonly byte[] Icon = [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, 3];

	/// <summary>acme shares, pushed: its icon for the libraries, its LICENSE for every repository. Returns its work clone.</summary>
	protected TempGitRepository ShareFromAcme()
	{
		var work = Clone(_acmeOrigin);
		Directory.CreateDirectory(Path.Combine(work.Path, "res"));
		File.WriteAllBytes(Path.Combine(work.Path, "res", "icon.png"), Icon);
		work.WriteFile("LICENSE", "MIT acme\n");
		var space = Path.Combine(work.Path, "_fx", "space.yaml");
		// (the space.yaml of the fixture does not end in a new line)
		File.WriteAllText(space, File.ReadAllText(space).Replace("\r\n", "\n").Replace("paths:\n", "paths:\n  - path: res/\n  - path: LICENSE\n").TrimEnd('\n')
			+ "\nshares:\n  - path: res/icon.png\n    applies-to:\n      tags: [library]\n  - path: LICENSE\n");
		work.CommitAll("shares");
		work.Git("push", "--quiet", "origin", "HEAD");
		return work;
	}
}
