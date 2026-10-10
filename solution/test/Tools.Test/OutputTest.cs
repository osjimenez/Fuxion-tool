using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Cli.Sync;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The faces of the command line (plan O, O3.1): what fx can do with the terminal, NDJSON, cancellation.</summary>
public sealed class OutputTest
{
	static GlobalSettings Settings(OutputFormat output = OutputFormat.Human, bool plain = false, bool nonInteractive = false)
		=> new(Environment.CurrentDirectory, output, false, plain, nonInteractive);

	static Func<string, string?> Env(string variables)
	{
		var pairs = variables.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => (Name: v[..v.IndexOf('=')], Value: v[(v.IndexOf('=') + 1)..])).ToList();
		return name => pairs.FirstOrDefault(p => p.Name == name).Value;
	}

	[Theory(DisplayName = "capabilities: ask, animate and colour, each apart; the options win")]
	// variables, output, plain, non-interactive, input redirected, output redirected → prompt, animate, colour
	[InlineData("", "human", false, false, false, false, true, true, true)]
	[InlineData("CI=true", "human", false, false, false, false, false, false, true)]
	[InlineData("GITHUB_ACTIONS=true", "human", false, false, false, true, false, false, true)]
	[InlineData("", "human", false, false, false, true, false, false, false)]
	[InlineData("FORCE_COLOR=1", "human", false, false, false, true, false, false, true)]
	[InlineData("CLAUDECODE=1", "human", false, false, false, false, false, false, true)]
	[InlineData("", "human", false, false, true, false, false, true, true)]
	[InlineData("", "human", false, true, false, false, false, false, true)]
	[InlineData("", "human", true, false, false, false, true, true, false)]
	[InlineData("NO_COLOR=1", "human", false, false, false, false, true, true, false)]
	[InlineData("TERM=dumb", "human", false, false, false, false, true, false, false)]
	[InlineData("", "json", false, false, false, false, false, false, false)]
	[InlineData("", "ndjson", false, false, false, false, false, false, false)]
	public void Capabilities(string variables, string output, bool plain, bool nonInteractive, bool inputRedirected, bool outputRedirected,
		bool prompt, bool animate, bool color)
	{
		var format = output switch { "json" => OutputFormat.Json, "ndjson" => OutputFormat.Ndjson, _ => OutputFormat.Human };
		var c = TerminalCapabilities.Detect(Settings(format, plain, nonInteractive), Env(variables), inputRedirected, outputRedirected);
		Assert.Equal((prompt, animate, color), (c.CanPrompt, c.CanAnimate, c.CanColor));
	}

	[Fact(DisplayName = "--output ndjson: a start line, one line per event, and the document last")]
	public void Ndjson()
	{
		using var meta = new TempGitRepository();
		using var origin = new TempGitRepository();
		origin.WriteFile("FxPlus.slnx", "<Solution />\n");
		origin.Commit("c1");
		meta.WriteFile(Path.Combine("_fx", "workspace.yaml"), $"""
			repositories:
			  - name: plus
			    path: plus/repo
			    url: {origin.Path.Replace('\\', '/')}
			    mount: mandatory
			    tags: [dotnet]
			""");
		meta.Commit("manifest");

		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync", "--output", "ndjson"], stdout, new StringWriter(), meta.Path));
		var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
		Assert.Equal("start", lines[0].GetProperty("type").GetString());
		Assert.Equal("fx-events/1", lines[0].GetProperty("schema").GetString());
		Assert.Contains(lines, l => l.TryGetProperty("type", out var t) && t.GetString() == "step-started"
		                            && l.GetProperty("step").GetString() == "clone" && l.GetProperty("repository").GetString() == "plus");
		Assert.Contains(lines, l => l.TryGetProperty("type", out var t) && t.GetString() == "step-finished"
		                            && l.GetProperty("step").GetString() == "clone" && l.GetProperty("succeeded").GetBoolean());
		var document = lines[^1];
		Assert.Equal("fx-sync/1", document.GetProperty("schema").GetString());
		Assert.True(document.GetProperty("ok").GetBoolean());
	}

	[Fact(DisplayName = "a cancelled command ends with 130")]
	public void Cancelled()
	{
		using var meta = new TempGitRepository();
		meta.WriteFile(Path.Combine("_fx", "workspace.yaml"), "repositories: []\n");
		meta.Commit("manifest");
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();
		var stderr = new StringWriter();
		var settings = new GlobalSettings(meta.Path, OutputFormat.Human, false, false);
		Assert.Equal(FxConsole.Cancelled, SyncCommand.Run(new(settings, "workspace", DryRun: false), new(new StringWriter(), stderr, settings), doctor: false, cancelled.Token));
		Assert.Contains("Cancelled.", stderr.ToString());
	}

	[Fact(DisplayName = "info diagnostics only with -v")]
	public void InfoOnlyVerbose()
	{
		using var folder = new TempGitRepository();
		var stderr = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync"], new StringWriter(), stderr, folder.Path));
		Assert.DoesNotContain("fx.nothing-to-do", stderr.ToString());
		stderr = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync", "-v"], new StringWriter(), stderr, folder.Path));
		Assert.Contains("info fx.nothing-to-do", stderr.ToString());
	}
}
