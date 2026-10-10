using System;
using System.Linq;

namespace Fuxion.Tools.Cli;

/// <summary>
/// What fx can do with the terminal it runs in (plan O, decision 4), each one apart, as the Aspire CLI does: ask
/// (prompts), animate (a live region that redraws itself) and use colours. A CI log shows colours but cannot animate
/// or answer; a pipe or a coding agent, none of them. The explicit options always win: <c>--non-interactive</c>, no
/// asking nor animating; <c>--plain</c>, no colours; <c>--output json|ndjson</c>, none of the three.
/// </summary>
public sealed record TerminalCapabilities(bool CanPrompt, bool CanAnimate, bool CanColor, bool IsCi, bool IsAgent)
{
	/// <summary>Set by the CI servers fx knows (besides <c>CI=true</c>).</summary>
	static readonly string[] CiVariables = ["GITHUB_ACTIONS", "TF_BUILD", "JENKINS_URL", "BUILDKITE", "GITLAB_CI", "CIRCLECI", "TEAMCITY_VERSION", "APPVEYOR"];

	/// <summary>Set by coding agents when they run a command: a hint (the options win), never a guarantee.</summary>
	static readonly string[] AgentVariables = ["CLAUDECODE", "AI_AGENT", "AGENT", "CURSOR_AGENT", "GEMINI_CLI", "CODEX_SANDBOX"];

	/// <summary>Nothing interactive: what a test, a pipe or a machine gets.</summary>
	public static TerminalCapabilities None { get; } = new(false, false, false, false, false);

	public static TerminalCapabilities Detect(GlobalSettings settings, Func<string, string?> environment, bool inputRedirected, bool outputRedirected)
	{
		bool Set(string name) => !string.IsNullOrEmpty(environment(name));
		var ci = environment("CI") is "true" or "1" or "True" or "TRUE" || CiVariables.Any(Set);
		var agent = AgentVariables.Any(Set);
		var dumb = string.Equals(environment("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
		var human = settings.Output == OutputFormat.Human;
		var interactive = human && !settings.NonInteractive && !ci && !agent && !outputRedirected;
		var forceColor = Set("FORCE_COLOR") || Set("CLICOLOR_FORCE");
		return new(
			CanPrompt: interactive && !inputRedirected,
			CanAnimate: interactive && !dumb,
			CanColor: human && !settings.Plain && !Set("NO_COLOR") && !dumb && (!outputRedirected || forceColor || Set("GITHUB_ACTIONS") || Set("TF_BUILD")),
			IsCi: ci,
			IsAgent: agent);
	}
}
