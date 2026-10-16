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

/// <summary>Workspaces inside workspaces (plan P, P4-P6), on the sample tree: inheritance, --only, guests, the highest copy.</summary>
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
