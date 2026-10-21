using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fuxion.Tool.Core.Docs;

/// <summary>Where a document of a type lives (the standard, its layout rule): in <c>_records/AAAA/MM/</c>, or in a human folder.</summary>
public enum DocTemporality
{
	Historical,
	Stable
}

/// <summary>What a status means to the logic of the module (the SPEC of the module, 13.2).</summary>
public enum DocStatusClass
{
	/// <summary>Being worked on.</summary>
	Open,
	/// <summary>In force.</summary>
	Current,
	/// <summary>Finished: what it left lives in other documents.</summary>
	Closed,
	/// <summary>Replaced, or on its way out.</summary>
	Replaced,
	/// <summary>Without effect.</summary>
	Void
}

/// <summary>
/// A type of document as its schema declares it, with what the logic of the module needs of it: its temporality, its
/// statuses and the class of each one, its profiles, and whether it takes a <c>generation</c>. Read from the annotations of
/// the schemas, never from a list in the code (plan U, ADR "esquemas cerrados, con costuras"). <see cref="Required"/>, the
/// properties a sidecar of the type must have: those of the base and its own.
/// </summary>
public sealed record DocType(
	string Name,
	DocTemporality Temporality,
	IReadOnlyList<string> Statuses,
	IReadOnlyDictionary<string, DocStatusClass> Classes,
	IReadOnlyList<string> Profiles,
	bool ProfileRequired,
	bool GenerationAllowed,
	bool GenerationRequired,
	IReadOnlyList<string> Required)
{
	/// <summary>The prefix of its file names: the type in upper case (<c>ADR-</c>).</summary>
	public string Token => Name.ToUpperInvariant();

	/// <summary>The status a new document starts in: <c>draft</c> if the type has it; if not, its first in force.</summary>
	public string InitialStatus => Statuses.Contains("draft") ? "draft" : Statuses.First(s => Classes[s] == DocStatusClass.Current);

	public DocStatusClass ClassOf(string status) => Classes[status];

	/// <summary>A historical document out of <see cref="DocStatusClass.Open"/> does not change its content.</summary>
	public bool IsFrozen(string status) => Temporality == DocTemporality.Historical && Classes.TryGetValue(status, out var c) && c != DocStatusClass.Open;

	/// <summary>
	/// Whether a document of the type may go from <paramref name="from"/> to <paramref name="to"/> (plan U, decision 21):
	/// to another of its statuses, never back to <c>draft</c>, and a frozen one never back to an open one.
	/// </summary>
	public bool CanMove(string from, string to)
		=> to != from && Statuses.Contains(to) && to != "draft" && !(IsFrozen(from) && ClassOf(to) == DocStatusClass.Open);

	/// <summary>The status a document of the type takes when another supersedes it: <c>superseded</c>, or its first of the class replaced; null if it has none.</summary>
	public string? SupersededStatus => Statuses.Contains("superseded") ? "superseded" : Statuses.FirstOrDefault(s => Classes[s] == DocStatusClass.Replaced);
}

/// <summary>The thirteen types of the standard, from their schemas.</summary>
public static class DocTypes
{
	static readonly Lazy<IReadOnlyList<DocType>> Loaded = new(() => DocsSchemas.Texts
		.Where(t => t.Key.StartsWith("types/", StringComparison.Ordinal))
		.Select(t => Read(t.Key["types/".Length..^".schema.json".Length], t.Value))
		.OrderBy(t => t.Name, StringComparer.Ordinal)
		.ToList());

	public static IReadOnlyList<DocType> All => Loaded.Value;

	/// <summary>The type <paramref name="name"/>; null if the standard has none.</summary>
	public static DocType? Find(string name) => All.FirstOrDefault(t => t.Name == name);

	/// <summary>The type whose file names start with <paramref name="token"/> (<c>ADR</c>); null if none.</summary>
	public static DocType? FromToken(string token) => All.FirstOrDefault(t => t.Token == token);

	/// <summary>The properties every sidecar must have, from the base.</summary>
	static readonly Lazy<IReadOnlyList<string>> BaseRequired = new(() =>
	{
		using var document = JsonDocument.Parse(DocsSchemas.Texts["base.schema.json"]);
		return document.RootElement.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToList();
	});

	/// <summary>A type from its schema: its annotations and the part of its <c>allOf</c> that specializes the base.</summary>
	static DocType Read(string name, string text)
	{
		using var document = JsonDocument.Parse(text);
		var root = document.RootElement;
		var temporality = root.GetProperty("x-docs-temporality").GetString() == "historical" ? DocTemporality.Historical : DocTemporality.Stable;
		var own = root.GetProperty("allOf")[1];
		var properties = own.GetProperty("properties");
		var statuses = properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(s => s.GetString()!).ToList();
		var classes = root.GetProperty("x-docs-status-classes").EnumerateObject()
			.ToDictionary(c => c.Name, c => Enum.Parse<DocStatusClass>(c.Value.GetString()!, ignoreCase: true), StringComparer.Ordinal);
		var profiles = properties.TryGetProperty("profile", out var profile) ? profile.GetProperty("enum").EnumerateArray().Select(p => p.GetString()!).ToList() : [];
		var required = own.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
		return new(name, temporality, statuses, classes, profiles, required.Contains("profile"), properties.TryGetProperty("generation", out _),
			required.Contains("generation"), [.. BaseRequired.Value, .. required]);
	}
}
