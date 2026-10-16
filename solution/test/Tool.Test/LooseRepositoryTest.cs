using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Workspace;
using Fuxion.Tool.Core.Worktree;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using Xunit;
using static Fuxion.Tool.Test.Worktrees;

namespace Fuxion.Tool.Test;

/// <summary>
/// A repository cloned on its own, on the sample tree: its parent read from the remote (in the cache, FX_CACHE) and its
/// worktrees in the folder of the user (FX_WORKTREES). Both are variables of the process: these tests go alone.
/// </summary>
[Collection(nameof(ProcessCollection))]
public sealed class LooseRepositoryTest : SampleTree
{
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
			Assert.True(Directory.Exists(Path.Combine(cache, Path.GetFileName(CacheFolder(_acmeOrigin)))));

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
			Assert.True(File.Exists(Path.Combine(CacheFolder(_acmeOrigin), "res", "icon.png")));
			return 0;
		});
	}

	[Fact(DisplayName = "a repository on its own: its worktrees in the folder of the user")]
	public void Loose()
	{
		var folder = Directory.CreateTempSubdirectory("fx-worktrees-").FullName;
		var before = Environment.GetEnvironmentVariable("FX_WORKTREES");
		Environment.SetEnvironmentVariable("FX_WORKTREES", folder);
		try
		{
			var loose = Clone(_sharedOrigin);
			var root = Ok(WorktreeModule.Add(loose.Path, "fix/l", null, false, cancellationToken: Token)).Root!;
			Assert.StartsWith(FxLayout.Normalize(folder), root, StringComparison.OrdinalIgnoreCase);
			Assert.True(File.Exists(Path.Combine(root, "Shared.csproj")));
			Assert.Equal("fix/l", Assert.Single(WorktreeModule.List(loose.Path)).Card.Branch);
			Ok(WorktreeModule.Remove(loose.Path, "fix/l", cancellationToken: Token));
			Assert.False(Directory.Exists(root));
			Assert.False(Directory.Exists(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))));
		}
		finally
		{
			Environment.SetEnvironmentVariable("FX_WORKTREES", before);
			new TempGitRepository(init: false, path: folder).Dispose();
		}
	}
}
