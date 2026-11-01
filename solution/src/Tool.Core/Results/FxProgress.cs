using System;
using System.Diagnostics;

namespace Fuxion.Tool.Core.Results;

/// <summary>
/// The progress channel of the core (plan O, decision 13), its port (the ADR of the diagnostics, 5): the modules say what
/// they are doing, as they do it, without knowing who listens (a live region, plain lines, NDJSON, the full screen, a
/// test; tomorrow the seam of the hub, as the <c>OnNext</c> of an observer). Events come from several threads (the work on
/// the repositories runs in parallel): a listener takes them to its own. A diagnostic is an event once, when the
/// collector of a module takes it (<see cref="FxDiagnostics"/>): operations return their diagnostics, they do not emit
/// them.
/// </summary>
public interface IFxProgress
{
	void Report(FxEvent e);
}

/// <summary>
/// Something that happened in a module, about a repository or the space (no repository). A closed hierarchy of sealed,
/// immutable records, only data (texts, numbers, durations and diagnostics): those of the core, and no other.
/// </summary>
public abstract record FxEvent
{
	private protected FxEvent(string module, string? repository)
	{
		Module = module;
		Repository = repository;
	}

	/// <summary>The module it happened in.</summary>
	public string Module { get; }

	/// <summary>The repository it is about; none: the space.</summary>
	public string? Repository { get; }
}

/// <summary>A step starts: <c>clone</c>, <c>fetch</c>, <c>status</c>, <c>pull</c>, <c>remote</c>, <c>generate</c>…</summary>
public sealed record FxStepStarted(string Module, string? Repository, string Step) : FxEvent(Module, Repository);

/// <summary>A step ends, well or not, and how long it took.</summary>
public sealed record FxStepFinished(string Module, string? Repository, string Step, bool Succeeded, TimeSpan Elapsed) : FxEvent(Module, Repository);

/// <summary>A diagnostic, as soon as a module finds it.</summary>
public sealed record FxDiagnosticFound(FxDiagnostic Diagnostic) : FxEvent(Diagnostic.Module, Diagnostic.Repository);

public static class FxProgressExtensions
{
	/// <summary>Runs a step between its started and finished events; it succeeded if <paramref name="step"/> says so.</summary>
	public static T Step<T>(this IFxProgress? progress, string module, string? repository, string name, Func<T> step, Func<T, bool> succeeded)
	{
		progress?.Report(new FxStepStarted(module, repository, name));
		var clock = Stopwatch.StartNew();
		var ok = false;
		try
		{
			var result = step();
			ok = succeeded(result);
			return result;
		}
		finally
		{
			progress?.Report(new FxStepFinished(module, repository, name, ok, clock.Elapsed));
		}
	}

	/// <summary>A step that succeeds when it does not throw.</summary>
	public static T Step<T>(this IFxProgress? progress, string module, string? repository, string name, Func<T> step)
		=> progress.Step(module, repository, name, step, _ => true);

	/// <summary>A step that fails when its response is an error.</summary>
	public static Response<T> Step<T>(this IFxProgress? progress, string module, string? repository, string name, Func<Response<T>> step) where T : notnull
		=> progress.Step(module, repository, name, step, r => r.IsSuccess);

	/// <summary>A step that fails when its response is an error (none is no failure).</summary>
	public static ResponseMaybe<T> Step<T>(this IFxProgress? progress, string module, string? repository, string name, Func<ResponseMaybe<T>> step) where T : notnull
		=> progress.Step(module, repository, name, step, r => r.IsSuccess);

	/// <summary>
	/// Only the steps: for a module run inside another, which collects its diagnostics (and reports them itself, with
	/// their final paths).
	/// </summary>
	public static IFxProgress? StepsOnly(this IFxProgress? progress) => progress is null ? null : new StepsFilter(progress);

	sealed class StepsFilter(IFxProgress inner) : IFxProgress
	{
		public void Report(FxEvent e)
		{
			if (e is not FxDiagnosticFound)
				inner.Report(e);
		}
	}
}
