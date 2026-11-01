using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using Fuxion.Tool.Core.Results;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The helpers of fx over the responses of Fuxion: the error of an operation, its outcome, its diagnostics read back,
/// the collector and the steps.
/// </summary>
public sealed class FxResponsesTest
{
	static readonly FxDiagnostic Warning1 = FxDiagnostic.Warning("test.first", FxText.Plain("first"), "a.yaml", 1);
	static readonly FxDiagnostic Error2 = FxDiagnostic.Error("test.second", FxText.Plain("second"), "a.yaml", 2);
	static readonly FxDiagnostic Warning3 = FxDiagnostic.Warning("test.third", FxText.Plain("third"), "a.yaml", 3);
	static readonly FxDiagnostic Error4 = FxDiagnostic.Error("test.fourth", FxText.Plain("fourth"), "b.yaml", 4);
	static readonly FxDiagnostic Info5 = FxDiagnostic.Info("test.fifth", FxText.Plain("fifth"));

	sealed class Events : IFxProgress
	{
		public readonly List<FxEvent> All = [];

		public void Report(FxEvent e)
		{
			lock (All)
				All.Add(e);
		}
	}

	[Fact(DisplayName = "an error: its category, its diagnostics in the order they were found, the message of its first error and where it was made")]
	public void Error_Of()
	{
		var error = FxErrors.Of(HttpStatusCode.Conflict, [Warning1, Error2, Warning3, Error4]);
		Assert.Equal(HttpStatusCode.Conflict, error.Type);
		Assert.Equal(HttpStatusCode.Conflict, error.Category);
		Assert.Equal([Warning1, Error2, Warning3, Error4], error.Diagnostics);
		Assert.Equal("second", error.Message);
		Assert.Equal(nameof(Error_Of), error.Source?.MethodName);
		Assert.EndsWith(nameof(FxResponsesTest) + ".cs", error.Source?.File);
		Assert.Null(error.InnerErrors);
		Assert.Throws<ArgumentException>(() => FxErrors.Of(HttpStatusCode.Conflict, [Warning1]));
	}

	[Fact(DisplayName = "an outcome: an error if any is one, with all; otherwise its value, with its warnings; the category is the one given")]
	public void Outcome()
	{
		var failed = FxResponse.Outcome(42, [Warning1, Error2, Warning3], HttpStatusCode.BadRequest);
		Assert.True(failed is Error);
		Assert.Equal([Warning1, Error2, Warning3], failed.Diagnostics);
		Assert.Equal(HttpStatusCode.BadRequest, ((Error)failed).Category);

		var succeeded = FxResponse.Outcome(42, [Warning1, Warning3, Info5], HttpStatusCode.BadRequest);
		Assert.True(succeeded is int value && value == 42);
		Assert.Equal([Warning1, Warning3, Info5], succeeded.Diagnostics);
		Assert.Equal([Warning1, Warning3, Info5], (ImmutableArray<FxDiagnostic>)succeeded.Extensions[FxResponse.DiagnosticsKey]!);

		var clean = FxResponse.Outcome("value", [], HttpStatusCode.Conflict);
		Assert.Empty(clean.Diagnostics);
		Assert.Empty(clean.Extensions);
		Assert.Throws<ArgumentException>(() => FxResponse.Success(42, [Error2]));
	}

	[Fact(DisplayName = "an outcome that may find nothing: none, with its warnings, is no failure")]
	public void OutcomeMaybe()
	{
		var none = FxResponse.OutcomeMaybe<string>(null, [Warning1], HttpStatusCode.Conflict);
		Assert.True(none is None);
		Assert.True(none.IsSuccess);
		Assert.Equal([Warning1], none.Diagnostics);
		Assert.True(FxResponse.OutcomeMaybe("x", [], HttpStatusCode.Conflict) is string);
		var failed = FxResponse.OutcomeMaybe<string>(null, [Error2], HttpStatusCode.NotFound);
		Assert.True(failed is Error { Type: HttpStatusCode.NotFound });
		Assert.Equal([Error2], failed.Diagnostics);
	}

	[Fact(DisplayName = "with diagnostics found before: they go first, in a success and in an error")]
	public void WithDiagnostics()
	{
		var success = FxResponse.Success(1, [Warning3]).WithDiagnostics([Warning1]);
		Assert.Equal([Warning1, Warning3], success.Diagnostics);
		Assert.Throws<ArgumentException>(() => FxResponse.Success(1, []).WithDiagnostics([Error2]));

		Response<int> failed = FxErrors.Of(HttpStatusCode.Conflict, [Error4]);
		var both = failed.WithDiagnostics([Warning1, Error2]);
		Assert.Equal([Warning1, Error2, Error4], both.Diagnostics);
		Assert.Equal("second", ((Error)both).Message);
		Assert.Equal(HttpStatusCode.Conflict, ((Error)both).Category);
		Assert.Equal(((Error)failed).Source, ((Error)both).Source);
	}

	[Fact(DisplayName = "an error that is not fx's is one diagnostic, fx.error, with its message; an inner one comes after the own")]
	public void Foreign()
	{
		var foreign = Error.NotFound("Nothing there.");
		var diagnostic = Assert.Single(foreign.Diagnostics);
		Assert.Equal((FxErrors.Foreign, FxSeverity.Error, "Failed: Nothing there."), (diagnostic.Code, diagnostic.Severity, diagnostic.Message.English));
		Assert.Equal(HttpStatusCode.NotFound, foreign.Category);
		Assert.Equal(HttpStatusCode.InternalServerError, Error.Custom(message: "untyped").Category);

		var nested = FxErrors.Of(HttpStatusCode.Conflict, [Error2]) with { InnerErrors = [foreign, FxErrors.Of(HttpStatusCode.Conflict, [Warning3, Error4])] };
		Assert.Equal([Error2, foreign.Diagnostics[0], Warning3, Error4], nested.Diagnostics);
		var wrapped = foreign.Wrap("Wrapped.");
		Assert.Equal(["Failed: Wrapped.", "Failed: Nothing there."], wrapped.Diagnostics.Select(d => d.Message.English));
		Assert.Equal([Warning1, foreign.Diagnostics[0]], foreign.WithDiagnostics([Warning1]).Diagnostics);
	}

	[Fact(DisplayName = "the collector takes the diagnostics of a response: an event each, with the repository of the module")]
	public void Collector()
	{
		var events = new Events();
		var diagnostics = new FxDiagnostics(events, "plus");
		diagnostics.AddRange(FxResponse.Outcome(1, [Warning1, Error2], HttpStatusCode.Conflict));
		diagnostics.AddRange(FxResponse.Success("x", [Warning3]));
		diagnostics.AddRange(FxResponse.OutcomeMaybe<string>(null, [Info5], HttpStatusCode.Conflict));
		diagnostics.AddRange(FxErrors.Of(HttpStatusCode.Conflict, Error4 with { Repository = "oss" }));
		Assert.Equal(["test.first", "test.second", "test.third", "test.fifth", "test.fourth"], diagnostics.Select(d => d.Code));
		Assert.Equal(["plus", "plus", "plus", "plus", "oss"], events.All.Cast<FxDiagnosticFound>().Select(e => e.Repository));
	}

	[Fact(DisplayName = "a step whose response is an error has failed; a success or a none, not")]
	public void Step()
	{
		var events = new Events();
		IFxProgress progress = events;
		var failed = progress.Step("test", "plus", "read", () => (Response<int>)FxErrors.Of(HttpStatusCode.Conflict, Error2));
		Assert.True(failed is Error);
		progress.Step("test", "plus", "read", () => FxResponse.Success(1, []));
		progress.Step("test", "plus", "find", () => (ResponseMaybe<int>)None.Value);
		Assert.Equal([false, true, true], events.All.OfType<FxStepFinished>().Select(f => f.Succeeded));
		Assert.Equal(3, events.All.OfType<FxStepStarted>().Count());
	}

	[Fact(DisplayName = "default(Response) is neither a success nor an error: the helpers reject it")]
	public void Default()
	{
		Assert.Throws<ArgumentException>(() => default(Response<int>).Diagnostics);
		Assert.Throws<ArgumentException>(() => default(ResponseMaybe<int>).Diagnostics);
		Assert.Throws<ArgumentException>(() => default(Response<int>).WithDiagnostics([Warning1]));
		Assert.Throws<ArgumentException>(() => new FxDiagnostics().AddRange(default(Response<string>)));
	}
}
