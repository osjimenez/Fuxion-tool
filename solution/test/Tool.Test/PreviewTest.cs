using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// What <c>fx sync</c> would do with changes not written yet (plan O, O4.3a): the core reads them through
/// <see cref="FxFiles"/>, and a dry run says what it would do with the repositories instead of what is wrong.
/// </summary>
public sealed class PreviewTest : SampleTree
{
	static (int ExitCode, string Out, string Err) Fx(string root, params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = InProcess.Run(args, stdout, stderr, root);
		return (exitCode, stdout.ToString(), stderr.ToString());
	}

	static IReadOnlyList<FxModuleResult> DryRun(string root, string file, string text)
	{
		using (FxFiles.Overlay([KeyValuePair.Create(file, text)]))
			return SyncEngine.Sync(root, new(DryRun: true), cancellationToken: TestContext.Current.CancellationToken);
	}

	[Fact(DisplayName = "sync --dry-run with a change in place of its file: what sync would write with it, and the file as it was")]
	public void PendingChange()
	{
		var acme = Acme();
		var dotnet = Path.Combine(acme.Path, "_fx", "dotnet", "dotnet.yaml");
		var before = File.ReadAllText(dotnet);
		var results = DryRun(acme.Path, dotnet, before.Replace("net10.0;net11.0", "net11.0"));
		var files = results.SelectMany(r => r.Files).Where(f => f.Status == SyncFileStatus.WouldWrite).Select(f => f.Path.Replace('\\', '/')).ToList();
		// down to the last level: each one reads what the one above would leave
		Assert.Contains(files, f => f.EndsWith("repo-w1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.Contains(files, f => f.EndsWith("repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.Contains(files, f => f.EndsWith("repo-c2/_fx/dotnet/dotnet.g.props"));
		Assert.Contains(files, f => f.EndsWith("apps/c3/_fx/dotnet/dotnet.g.props"));
		// repo-c1 has its own CoreFrameworks: the nearest wins
		Assert.DoesNotContain(files, f => f.EndsWith("repo-c1/_fx/dotnet/dotnet.g.props"));
		Assert.Equal(before, File.ReadAllText(dotnet));
		// without it, nothing
		Assert.DoesNotContain(SyncEngine.Sync(acme.Path, new(DryRun: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Files),
			f => f.Status == SyncFileStatus.WouldWrite);
	}

	[Fact(DisplayName = "a dry run says what sync would do with the repositories (would clone, park, bring back); the doctor, what is wrong")]
	public void WouldDo()
	{
		var acme = Acme();
		var manifest = Path.Combine(acme.Path, "_fx", "mounts", "mounts.yaml");
		var added = ManifestEditor.AddRepository(acme.Path, "https://example.invalid/extra.git", "extra", required: true);
		var results = DryRun(acme.Path, manifest, added.After.Text);
		Assert.Contains(results.SelectMany(r => r.Actions), a => a is { Repository: "extra", Action: "would-clone" });
		Assert.DoesNotContain(results.SelectMany(r => r.Diagnostics), d => d.Severity == FxSeverity.Error);

		// shared, manual and unmounted (both pending): sync would park it; the doctor, that it is not parked
		var manual = ManifestEditor.SetRepository(acme.Path, "shared", "required", []);
		var state = Path.Combine(acme.Path, "_fx", "mounts", "~$state.yaml");
		using (FxFiles.Overlay([KeyValuePair.Create(manifest, manual.After.Text), KeyValuePair.Create(state, MountsState.Load(acme.Path).With("shared", false).ToYaml())]))
		{
			Assert.Contains(SyncEngine.Sync(acme.Path, new(DryRun: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Actions),
				a => a is { Repository: "shared", Action: "would-park" });
			Assert.Contains(SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics),
				d => d.Code == MountsModule.NotParked);
		}
		Assert.True(Directory.Exists(Path.Combine(acme.Path, "libs", "shared")));
	}

	[Fact(DisplayName = "edits over pending ones: each reads the text the last one left")]
	public void Chained()
	{
		var acme = Acme();
		var manifest = Path.Combine(acme.Path, "_fx", "mounts", "mounts.yaml");
		var first = ManifestEditor.TagRepository(acme.Path, "repo-w1", ["library"]);
		Assert.True(first.Changed);
		using (FxFiles.Overlay([KeyValuePair.Create(manifest, first.After.Text)]))
		{
			var second = ManifestEditor.TagRepository(acme.Path, "repo-w1", ["app"]);
			Assert.Equal(first.After.Text, second.Before);
			Assert.Contains("tags: [dotnet, product, library, app]", second.After.Text);
		}
		Assert.DoesNotContain("library", File.ReadAllText(manifest).Split("repo-w2")[0]);
	}

	[Fact(DisplayName = "a command that edits, with --dry-run: its diff and what sync would do with it; in JSON, its sync")]
	public void Cli()
	{
		var acme = Acme();
		var dry = Fx(acme.Path, "mount", "add", "https://example.invalid/extra.git", "extra", "--required", "--dry-run");
		Assert.Equal(0, dry.ExitCode);
		Assert.Contains("+  - name: extra", dry.Out);
		Assert.Contains("What fx sync would do with it:", dry.Out);
		Assert.Contains("extra", dry.Out.Split("What fx sync would do with it:")[1]);
		Assert.Contains("would-clone", dry.Out);

		var json = JsonDocument.Parse(Fx(acme.Path, "mount", "add", "https://example.invalid/extra.git", "extra", "--required", "--dry-run", "--output", "json").Out).RootElement;
		Assert.Contains(json.GetProperty("sync").EnumerateArray().SelectMany(m => m.GetProperty("mounts").EnumerateArray()),
			r => r.GetProperty("name").GetString() == "extra" && r.GetProperty("action").GetString() == "would-clone");
		// written: no sync, it is the next step
		var written = JsonDocument.Parse(Fx(acme.Path, "mount", "add", "https://example.invalid/extra.git", "extra", "--output", "json").Out).RootElement;
		Assert.True(written.GetProperty("written").GetBoolean());
		Assert.False(written.TryGetProperty("sync", out _));
	}
}
