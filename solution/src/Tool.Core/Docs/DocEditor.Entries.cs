using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>What a change of an entry says; what it does not say (null, empty), stays as it was.</summary>
public sealed record DocEntryEdit
{
	public string? Name { get; init; }
	public string? Title { get; init; }
	public string? Status { get; init; }
	public string? Summary { get; init; }
	public string? Key { get; init; }

	/// <summary>When it was born: a date (2026-01-16) or an instant; by default, now.</summary>
	public string? Created { get; init; }

	/// <summary>Tags it gets.</summary>
	public IReadOnlyList<string> Tags { get; init; } = [];

	/// <summary>Fields it gets, or loses (a null value).</summary>
	public IReadOnlyList<KeyValuePair<string, JsonElement?>> Fields { get; init; } = [];

	/// <summary>Relations it gets: a kind and its target, a document (its path, its identity) or an entry (its key, its identity).</summary>
	public IReadOnlyList<(string Kind, string Target)> Relations { get; init; } = [];

	/// <summary>Relations it loses.</summary>
	public IReadOnlyList<(string Kind, string Target)> Unrelate { get; init; } = [];
}

/// <summary>The entries of a register (plan U, decision 18): added, changed and closed, always through the register, which is finalized.</summary>
public static partial class DocEditor
{
	public const string NotARegister = "docs.not-a-register";
	public const string UnknownEntry = "docs.unknown-entry";
	public const string EntryTaken = "docs.entry-taken";
	public const string UnknownTarget = "docs.unknown-target";

	/// <summary>The status of a new entry, unless it says another.</summary>
	public const string OpenStatus = "open";

	/// <summary>The status of an entry that closes, unless it says another.</summary>
	public const string ClosedStatus = "closed";

	/// <summary>The entry of <paramref name="register"/> that <paramref name="selector"/> names: its name, its key or its identity.</summary>
	public static DocEntry? FindEntry(DocSidecar register, string selector)
		=> register.Entries.FirstOrDefault(e => e.Name == selector || e.Key == selector || string.Equals(e.Id, selector, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// The identity <paramref name="selector"/> names: a document (its path, its identity) or an entry of a register (its
	/// key, its identity); null if it names none, or several.
	/// </summary>
	public static string? Target(DocCatalog catalog, string selector, string currentDirectory, IReadOnlyList<DocDocument>? documents = null)
	{
		documents ??= DocChecker.Read(catalog);
		var ids = documents.GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().File, StringComparer.OrdinalIgnoreCase);
		if (catalog.Select(selector, currentDirectory, ids) is [var file] && documents.FirstOrDefault(d => d.File == file) is { } document)
			return document.Sidecar.Id;
		return documents.SelectMany(d => d.Sidecar.Entries).FirstOrDefault(e => e.Key == selector || string.Equals(e.Id, selector, StringComparison.OrdinalIgnoreCase))?.Id;
	}

	static Loaded? Register(DocChange change, DocFile file)
	{
		if (Load(change, file) is not { } register)
			return null;
		if (register.Type.Name == "register")
			return register;
		change.Error(NotARegister, new(NotARegister, register.Path, register.Type.Name), change.Display(file.Markdown!));
		return null;
	}

	static List<DocRelation> Resolve(DocChange change, string currentDirectory, IReadOnlyList<(string Kind, string Target)> relations)
	{
		var documents = DocChecker.Read(change.Catalog);
		var resolved = new List<DocRelation>();
		foreach (var (kind, target) in relations)
			if (!DocRelations.Kinds.Contains(kind))
				change.Error(DocChecker.InvalidRelation, new($"{DocChecker.InvalidRelation}.kind", kind, string.Join(", ", DocRelations.Kinds)));
			else if (Target(change.Catalog, target, currentDirectory, documents) is { } id)
				resolved.Add(new(kind, id));
			else
				change.Error(UnknownTarget, new(UnknownTarget, target));
		return resolved;
	}

	static IReadOnlyList<KeyValuePair<string, JsonElement>> Fields(IReadOnlyList<KeyValuePair<string, JsonElement>> fields, DocEntryEdit edit)
	{
		var result = fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
		foreach (var (name, value) in edit.Fields)
			if (value is { } set)
				result[name] = set;
			else
				result.Remove(name);
		return result.ToList();
	}

	/// <summary>
	/// <c>fx doc entry add</c>: a new entry at the end of <paramref name="file"/>, a register, which is finalized. Its name,
	/// from its title unless it says one, unique in the register; its status, <see cref="OpenStatus"/> unless it says one;
	/// in a register with keys, its key from the provider unless it says one (<paramref name="reserve"/> false, a dry run:
	/// the one it would get, reserving nothing; <paramref name="actor"/>, who reserves it).
	/// </summary>
	public static DocChange AddEntry(DocCatalog catalog, Func<string, string> display, string currentDirectory, DocFile file, string title, DocEntryEdit edit,
		DateTimeOffset now, string actor = "fx", bool reserve = true)
	{
		var change = new DocChange(catalog, display);
		if (Register(change, file) is not { } register)
			return change;
		title = title.Trim();
		if (title.Length == 0 || title.Contains('\n'))
			change.Error(InvalidTitle, new(InvalidTitle));
		var name = edit.Name ?? ProposeSlug(title);
		if (!DocChecker.SlugPattern().IsMatch(name))
			change.Error(DocChecker.InvalidName, new($"{DocChecker.InvalidName}.new-slug", name));
		else if (FindEntry(register.Sidecar, name) is { } taken && taken.Name == name)
			change.Error(EntryTaken, new(EntryTaken, name, register.Path));
		if (edit.Key is not null && register.Sidecar.Entries.Any(e => e.Key == edit.Key))
			change.Error(EntryTaken, new($"{EntryTaken}.key", edit.Key, register.Path));
		var created = edit.Created is null ? DocSidecar.Instant(now) : DocAdoption.Instant(edit.Created);
		if (created is null)
			change.Error(DocChecker.InvalidDates, new($"{DocChecker.InvalidDates}.format", "created", edit.Created));
		var relations = Resolve(change, currentDirectory, edit.Relations);
		if (!change.Ok)
			return change;
		var key = edit.Key;
		if (key is null && DocKeys.Keyed(catalog, file) is { } keyed)
		{
			var reserved = DocKeys.Reserve(catalog, file, keyed, register.Sidecar.Entries.Select(e => e.Key).OfType<string>(), actor, title, reserve);
			key = reserved.Key;
			if (reserved.Problem is not null)
				change.Diagnostics.Add(FxDiagnostic.Warning(DocKeys.ProvisionalKey, new(DocKeys.ProvisionalKey, key, reserved.Problem), display(file.Sidecar!),
					fix: FxFix.Run($"fx doc key {register.Path} --confirm")));
		}
		var entry = new DocEntry
		{
			Id = DocSidecar.NewId(DocSidecar.ParseInstant(created!)),
			Name = name,
			Key = key,
			Title = title,
			Status = edit.Status ?? OpenStatus,
			Summary = edit.Summary,
			Tags = edit.Tags.Distinct(StringComparer.Ordinal).ToList(),
			CreatedAt = created!,
			Relations = relations.Distinct().ToList(),
			Fields = Fields([], edit)
		};
		if (!Blocked(change, register))
			Save(change, register, register.Sidecar with { Entries = [.. register.Sidecar.Entries, entry] }, now);
		change.Verify();
		return change;
	}

	/// <summary><c>fx doc entry set</c>: an entry of <paramref name="file"/> with what <paramref name="edit"/> says; its name and its key stay.</summary>
	public static DocChange SetEntry(DocCatalog catalog, Func<string, string> display, string currentDirectory, DocFile file, string selector, DocEntryEdit edit,
		DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (Register(change, file) is not { } register)
			return change;
		if (FindEntry(register.Sidecar, selector) is not { } entry)
		{
			change.Error(UnknownEntry, new(UnknownEntry, selector, register.Path));
			return change;
		}
		var added = Resolve(change, currentDirectory, edit.Relations);
		var removed = Resolve(change, currentDirectory, edit.Unrelate);
		if (!change.Ok)
			return change;
		var changed = entry with
		{
			Title = edit.Title?.Trim() ?? entry.Title,
			Status = edit.Status ?? entry.Status,
			Summary = edit.Summary ?? entry.Summary,
			Tags = entry.Tags.Union(edit.Tags, StringComparer.Ordinal).ToList(),
			Relations = entry.Relations.Where(r => !removed.Contains(r)).Union(added).ToList(),
			Fields = Fields(entry.Fields, edit)
		};
		Replace(change, register, entry, changed, now);
		return change;
	}

	/// <summary><c>fx doc entry close</c>: an entry of <paramref name="file"/> closed now, in <paramref name="status"/> (<see cref="ClosedStatus"/> by default).</summary>
	public static DocChange CloseEntry(DocCatalog catalog, Func<string, string> display, DocFile file, string selector, string? status, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (Register(change, file) is not { } register)
			return change;
		if (FindEntry(register.Sidecar, selector) is not { } entry)
			change.Error(UnknownEntry, new(UnknownEntry, selector, register.Path));
		else if (!entry.Open)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.closed", entry.Name, entry.ClosedAt)));
		else
			Replace(change, register, entry, entry with { ClosedAt = DocSidecar.Instant(now), Status = status ?? ClosedStatus }, now);
		return change;
	}

	/// <summary>
	/// <c>fx doc key --confirm</c>: the provisional keys of <paramref name="file"/>, reserved now, in order; the register,
	/// finalized. What each was and what it is, in <paramref name="confirmed"/>.
	/// </summary>
	public static DocChange ConfirmKeys(DocCatalog catalog, Func<string, string> display, DocFile file, string actor, DateTimeOffset now, bool reserve,
		out IReadOnlyList<(string From, string To)> confirmed)
	{
		var change = new DocChange(catalog, display);
		var pairs = new List<(string, string)>();
		confirmed = pairs;
		if (Register(change, file) is not { } register)
			return change;
		if (DocKeys.Keyed(catalog, file) is not { } keyed)
		{
			change.Error(DocKeys.NotKeyed, new(DocKeys.NotKeyed, register.Path), display(ConfigFile(catalog)));
			return change;
		}
		var entries = register.Sidecar.Entries.ToList();
		var keys = entries.Select(e => e.Key).OfType<string>().Where(k => !DocKeys.IsProvisional(k)).ToList();
		for (var i = 0; i < entries.Count; i++)
		{
			if (entries[i].Key is not { } provisional || !DocKeys.IsProvisional(provisional))
				continue;
			var reserved = DocKeys.Reserve(catalog, file, keyed, keys, actor, entries[i].Title, reserve);
			if (reserved.Provisional)
			{
				change.Error(DocKeys.KeyFailed, reserved.Problem!, display(file.Sidecar!));
				return change;
			}
			keys.Add(reserved.Key);
			pairs.Add((provisional, reserved.Key));
			entries[i] = entries[i] with { Key = reserved.Key };
		}
		if (pairs.Count == 0)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.no-provisional", register.Path)));
		else if (!Blocked(change, register))
			Save(change, register, register.Sidecar with { Entries = entries }, now);
		change.Verify();
		return change;
	}

	/// <summary>The full path of the <c>docs.yaml</c> of <paramref name="catalog"/>.</summary>
	static string ConfigFile(DocCatalog catalog) => Layout.FxLayout.Full(catalog.Root, DocsModule.ConfigFile);

	/// <summary>The identity of the entry <paramref name="selector"/> names (its key, its identity) in any register of the catalog; null if none.</summary>
	public static string? EntryTarget(DocCatalog catalog, string selector)
		=> DocChecker.Read(catalog).SelectMany(d => d.Sidecar.Entries).FirstOrDefault(e => e.Key == selector || string.Equals(e.Id, selector, StringComparison.OrdinalIgnoreCase))?.Id;

	/// <summary><c>fx doc relate</c> to an entry of a register: as to a document, but an entry is of the type "entry" for the rules.</summary>
	public static DocChange RelateToEntry(DocCatalog catalog, Func<string, string> display, DocFile file, string kind, string entryId, bool remove, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (Load(change, file) is not { } source)
			return change;
		var relation = new DocRelation(kind, entryId);
		var has = source.Sidecar.Relations.Any(r => r.Kind == kind && string.Equals(r.TargetId, entryId, StringComparison.OrdinalIgnoreCase));
		if (remove ? !has : has)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new(remove ? $"{Unchanged}.no-relation" : $"{Unchanged}.relation", source.Path, kind, entryId)));
		else if (!remove && !DocRelations.Allows(kind, source.Type.Name, DocChecker.Entry))
			change.Error(DocChecker.InvalidRelation, new(DocChecker.InvalidRelation, kind, source.Type.Name, DocChecker.Entry, entryId, Allowed(DocRelations.Rule(kind))),
				display(file.Sidecar!));
		else if (!Blocked(change, source))
			Save(change, source, source.Sidecar with
			{
				Relations = remove
					? source.Sidecar.Relations.Where(r => !(r.Kind == kind && string.Equals(r.TargetId, entryId, StringComparison.OrdinalIgnoreCase))).ToList()
					: [.. source.Sidecar.Relations, relation]
			}, now);
		change.Verify();
		return change;
	}

	/// <summary>The register with <paramref name="entry"/> as <paramref name="changed"/>, finalized; if they are the same, nothing.</summary>
	static void Replace(DocChange change, Loaded register, DocEntry entry, DocEntry changed, DateTimeOffset now)
	{
		var entries = register.Sidecar.Entries.Select(e => ReferenceEquals(e, entry) ? changed : e).ToList();
		if ((register.Sidecar with { Entries = entries }).ToJson() == register.Sidecar.ToJson())
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.entry", entry.Name)));
		else if (!Blocked(change, register))
			Save(change, register, register.Sidecar with { Entries = entries }, now);
		change.Verify();
	}
}
