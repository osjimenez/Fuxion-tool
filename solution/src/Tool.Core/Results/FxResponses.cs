using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;

namespace Fuxion.Tool.Core.Results;

// fx on the responses of Fuxion: what can fail for something expected
// returns a Response; a failure is one Error, with its category (an HttpStatusCode) as its Type and the diagnostics of the
// operation, in the order they were found, as its Payload; the warnings of a success go with it, in its extension
// "diagnostics". No result type of fx's own: these helpers read and make the types of Fuxion.

/// <summary>The <see cref="Error"/> of an operation of fx: its category and its diagnostics.</summary>
public static class FxErrors
{
	/// <summary>The code of the diagnostic of an <see cref="Error"/> that is not fx's: its message.</summary>
	public const string Foreign = "fx.error";

	/// <summary>
	/// The error of an operation: its <paramref name="category"/> and its <paramref name="diagnostics"/> (its errors and the
	/// warnings that came with them, in order), and the exception that became it, if its detail helps to debug. Its
	/// message is the English one of its first error, for the logs; its source, where it was made in the code of fx.
	/// </summary>
	/// <exception cref="ArgumentException">No diagnostic is an error: an error of fx says what failed.</exception>
	public static Error Of(HttpStatusCode category, IEnumerable<FxDiagnostic> diagnostics, Exception? exception = null,
		[CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
	{
		var list = diagnostics.ToImmutableArray();
		var first = list.FirstOrDefault(d => d.Severity == FxSeverity.Error)
		            ?? throw new ArgumentException("The error of an operation of fx has at least one diagnostic of severity error.", nameof(diagnostics));
		return Error.Custom(first.Message.English, category, list, exception, callerMemberName: callerMemberName, callerFilePath: callerFilePath,
			callerLineNumber: callerLineNumber);
	}

	/// <summary>The error of an operation that found one problem.</summary>
	public static Error Of(HttpStatusCode category, FxDiagnostic diagnostic, Exception? exception = null,
		[CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
		=> Of(category, [diagnostic], exception, callerMemberName, callerFilePath, callerLineNumber);
}

/// <summary>The <see cref="Response{TSuccess}"/> of an operation of fx, from what it found.</summary>
public static class FxResponse
{
	/// <summary>The extension of a success with its warnings (<c>ImmutableArray&lt;FxDiagnostic&gt;</c>).</summary>
	public const string DiagnosticsKey = "diagnostics";

	/// <summary>
	/// What an operation gives: an <see cref="Error"/> of <paramref name="category"/> with all it found if any of
	/// <paramref name="diagnostics"/> is an error; otherwise <paramref name="value"/>, with them as its warnings.
	/// </summary>
	public static Response<T> Outcome<T>(T value, IEnumerable<FxDiagnostic> diagnostics, HttpStatusCode category,
		[CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where T : notnull
	{
		var list = diagnostics.ToImmutableArray();
		return list.Any(d => d.Severity == FxSeverity.Error)
			? FxErrors.Of(category, list, null, callerMemberName, callerFilePath, callerLineNumber)
			: Success(value, list);
	}

	/// <summary>As <see cref="Outcome{T}"/>, for an operation that may find nothing, which is no failure.</summary>
	public static ResponseMaybe<T> OutcomeMaybe<T>(T? value, IEnumerable<FxDiagnostic> diagnostics, HttpStatusCode category,
		[CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where T : notnull
	{
		var list = diagnostics.ToImmutableArray();
		if (list.Any(d => d.Severity == FxSeverity.Error))
			return FxErrors.Of(category, list, null, callerMemberName, callerFilePath, callerLineNumber);
		ResponseMaybe<T> response = value is null ? None.Value : value;
		return list.IsEmpty ? response : response.WithExtension(DiagnosticsKey, list);
	}

	/// <summary><paramref name="value"/>, with <paramref name="warnings"/> (none of them an error).</summary>
	public static Response<T> Success<T>(T value, IEnumerable<FxDiagnostic> warnings) where T : notnull
	{
		var list = warnings.ToImmutableArray();
		if (list.Any(d => d.Severity == FxSeverity.Error))
			throw new ArgumentException("A success carries no error: make its Outcome.", nameof(warnings));
		Response<T> response = value;
		return list.IsEmpty ? response : response.WithExtension(DiagnosticsKey, list);
	}

	internal static ImmutableArray<FxDiagnostic> Of(ImmutableExtensions<IResponse> extensions)
		=> extensions.TryGetValue(DiagnosticsKey, out var value) && value is ImmutableArray<FxDiagnostic> list ? list : [];

	internal static void Check(bool hasValue)
	{
		if (!hasValue)
			throw new ArgumentException("default(Response) is neither a success nor an error: no operation of fx returns it.");
	}
}

/// <summary>Reading the diagnostics of a response or an error, and adding to them.</summary>
public static class FxResponseExtensions
{
	extension(Error error)
	{
		/// <summary>The category of the error: its <see cref="Error.Type"/>, or <see cref="HttpStatusCode.InternalServerError"/> if it has none.</summary>
		public HttpStatusCode Category => error.Type is HttpStatusCode category ? category : HttpStatusCode.InternalServerError;

		/// <summary>
		/// Its diagnostics, in order: those of its payload, then those of its inner errors. An error that is not fx's (of
		/// another API of Fuxion) is one diagnostic, <c>fx.error</c>, with its message.
		/// </summary>
		public ImmutableArray<FxDiagnostic> Diagnostics
			=> error.InnerErrors is { Length: > 0 } inner ? [.. error.Own, .. inner.SelectMany(e => e.Diagnostics)] : error.Own;

		/// <summary>Those of its payload, or the one of an error that is not fx's.</summary>
		ImmutableArray<FxDiagnostic> Own => error.Payload is ImmutableArray<FxDiagnostic> list ? list
			: [FxDiagnostic.Error(FxErrors.Foreign, new(FxErrors.Foreign, error.Message ?? error.Type?.ToString() ?? nameof(Error)))];

		/// <summary>The same error, with <paramref name="diagnostics"/>, found before it, first.</summary>
		public Error WithDiagnostics(IEnumerable<FxDiagnostic> diagnostics)
		{
			var list = diagnostics.ToImmutableArray();
			if (list.IsEmpty)
				return error;
			ImmutableArray<FxDiagnostic> all = [.. list, .. error.Own];
			return error with { Payload = all, Message = all.First(d => d.Severity == FxSeverity.Error).Message.English };
		}
	}

	extension<T>(Response<T> response) where T : notnull
	{
		/// <summary>Its diagnostics: those of its error, or the warnings of its success.</summary>
		public ImmutableArray<FxDiagnostic> Diagnostics
		{
			get
			{
				FxResponse.Check(response.HasValue);
				return response is Error error ? error.Diagnostics : FxResponse.Of(response.Extensions);
			}
		}

		/// <summary>The same response, with <paramref name="diagnostics"/>, found before it, first (in a success, none of them an error).</summary>
		public Response<T> WithDiagnostics(IEnumerable<FxDiagnostic> diagnostics)
		{
			FxResponse.Check(response.HasValue);
			if (response is Error error)
				return error.WithDiagnostics(diagnostics);
			var list = diagnostics.ToImmutableArray();
			if (list.IsEmpty)
				return response;
			if (list.Any(d => d.Severity == FxSeverity.Error))
				throw new ArgumentException("A success carries no error: make its Outcome.", nameof(diagnostics));
			return response.WithExtension(FxResponse.DiagnosticsKey, list.AddRange(FxResponse.Of(response.Extensions)));
		}
	}

	extension<T>(ResponseMaybe<T> response) where T : notnull
	{
		/// <summary>Its diagnostics: those of its error, or the warnings of its success or its none.</summary>
		public ImmutableArray<FxDiagnostic> Diagnostics
		{
			get
			{
				FxResponse.Check(response.HasValue);
				return response is Error error ? error.Diagnostics : FxResponse.Of(response.Extensions);
			}
		}

		/// <summary>The same response, with <paramref name="diagnostics"/>, found before it, first (in a success or a none, none of them an error).</summary>
		public ResponseMaybe<T> WithDiagnostics(IEnumerable<FxDiagnostic> diagnostics)
		{
			FxResponse.Check(response.HasValue);
			if (response is Error error)
				return error.WithDiagnostics(diagnostics);
			var list = diagnostics.ToImmutableArray();
			if (list.IsEmpty)
				return response;
			if (list.Any(d => d.Severity == FxSeverity.Error))
				throw new ArgumentException("A success carries no error: make its Outcome.", nameof(diagnostics));
			return response.WithExtension(FxResponse.DiagnosticsKey, list.AddRange(FxResponse.Of(response.Extensions)));
		}
	}
}
