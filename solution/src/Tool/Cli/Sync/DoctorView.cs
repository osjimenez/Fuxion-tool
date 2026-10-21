using System.Collections.Generic;
using System.Linq;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Cli.Sync;

/// <summary>
/// <c>fx doctor</c> for people, in the style of <c>flutter doctor</c> (plan O, decision 8): a line per module,
/// <c>[✓]</c>, <c>[!]</c> or <c>[✗]</c>; under it, each problem with its repository, where it is and its code, and
/// the fix after an arrow; at the end, how many modules have problems. The <c>info</c> findings, only with
/// <c>--verbose</c>. On stdout: here the diagnostics are the result.
/// </summary>
static class DoctorView
{
	static readonly string[] Order = ["fx", "mounts", "dotnet", "harness", "version"];

	public static void Write(FxConsole console, IReadOnlyList<FxModuleResult> modules, IReadOnlyList<FxDiagnostic> diagnostics)
	{
		var shown = diagnostics.Where(d => d.Severity != FxSeverity.Info || console.Settings.Verbose).ToList();
		var names = modules.Select(m => m.Module)
			.Concat(shown.Select(d => d.Module))
			.Distinct()
			.OrderBy(n => System.Array.IndexOf(Order, n) is var i and >= 0 ? i : Order.Length)
			.ToList();
		var withProblems = 0;
		foreach (var name in names)
		{
			var own = shown.Where(d => d.Module == name).OrderByDescending(d => d.Severity).ToList();
			var errors = own.Count(d => d.Severity == FxSeverity.Error);
			var warnings = own.Count(d => d.Severity == FxSeverity.Warning);
			if (errors + warnings > 0)
				withProblems++;
			var mark = errors > 0 ? "[red][[✗]][/]" : warnings > 0 ? "[yellow][[!]][/]" : "[green][[✓]][/]";
			var counts = new List<string>();
			if (errors > 0)
				counts.Add(Texts.Get("doctor.errors", errors));
			if (warnings > 0)
				counts.Add(Texts.Get("doctor.warnings", warnings));
			var summary = counts.Count == 0 ? Texts.Get("doctor.module-ok") : string.Join(", ", counts);
			console.Line($"{mark} {FxConsole.Escape(name),-10} [gray]{FxConsole.Escape(summary)}[/]");
			foreach (var d in own)
			{
				var glyph = d.Severity switch
				{
					FxSeverity.Error => "[red]✗[/]",
					FxSeverity.Warning => "[yellow]![/]",
					_ => "[gray]i[/]"
				};
				var repository = d.Repository is null ? "" : $"[bold]{FxConsole.Escape(d.Repository)}[/] · ";
				var where = d.File is null ? "" : d.Line is null ? $"{d.File} · " : $"{d.File}:{d.Line} · ";
				console.Line($"    {glyph} {repository}{FxConsole.Escape(d.Message.ToString())} [gray]({FxConsole.Escape(where)}{FxConsole.Escape(d.Code)})[/]");
				if (d.Fix is not null)
					console.Line($"      [cyan]→[/]{FxConsole.Escape(FxConsole.Fix(d.Fix)[2..])}");
			}
		}
		console.Line(withProblems == 0
			? $"[green]{FxConsole.Escape(Texts.Get("doctor.no-issues"))}[/]"
			: $"[yellow]{FxConsole.Escape(Texts.Get("doctor.issues", withProblems))}[/]");
	}
}
