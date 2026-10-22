using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The views (the SPEC of the module, 5.4): documents of the type <c>view</c> that <c>fx sync</c> generates from their
/// recipe (<c>generation</c>), and nobody edits. Their recipes:
/// <list type="bullet">
/// <item><c>docs.status</c>, the dashboard of a folder: what asks for attention, what is open and what is in force, by type;</item>
/// <item><c>docs.catalog</c>, the documents of a folder (<c>docs.catalog/&lt;tag&gt;</c>, only those with the tag);</item>
/// <item><c>docs.backlog</c>, the board of a register: its open entries by status, and those closed.</item>
/// </list>
/// The same catalog gives the same bytes: in the language of <c>docs.yaml</c>, whoever runs it, and without the views
/// (each regeneration changes them).
/// </summary>
public static class DocViews
{
	public const string UnknownRecipe = "docs.unknown-recipe";
	public const string InvalidScope = "docs.invalid-scope";
	public const string ViewOutdated = "docs.view-outdated";

	const string Status = DocRecipes.Status;
	const string Catalog = DocRecipes.Catalog;
	const string Backlog = DocRecipes.Backlog;

	public static IReadOnlyList<string> Recipes => DocRecipes.All;

	public static bool Known(string recipe) => DocRecipes.Known(recipe);

	/// <summary>
	/// The text of the view <paramref name="title"/> with <paramref name="generation"/>, at <paramref name="at"/> (the full
	/// path of its Markdown), from <paramref name="documents"/>; null, with <paramref name="problem"/>, if its recipe or its
	/// scope are not valid.
	/// </summary>
	public static string? Generate(DocCatalog catalog, IReadOnlyList<DocDocument> documents, string title, DocGeneration generation, string at, out FxText? problem)
	{
		problem = null;
		if (!Known(generation.Recipe))
		{
			problem = new(UnknownRecipe, generation.Recipe, $"{string.Join(", ", Recipes)}, {Catalog}/<tag>");
			return null;
		}
		var scope = FxLayout.Full(catalog.Root, generation.Scope);
		string T(string key, params object?[] args) => new FxText(key, args).ToString(catalog.Config.Culture);
		var folder = Path.GetDirectoryName(at)!;
		string Link(string text, string target) => $"[{Escape(text)}]({Path.GetRelativePath(folder, target).Replace('\\', '/').Replace(" ", "%20")})";
		var text = new StringBuilder($"# {title}\n\n> {T("docs.view.generated", generation.Recipe, generation.Scope)}\n");

		if (generation.Recipe == Backlog)
		{
			if (documents.FirstOrDefault(d => Same(d.File.Markdown!, scope)) is not { Type.Name: "register" } register)
			{
				problem = new($"{InvalidScope}.register", generation.Scope);
				return null;
			}
			Backlogs(text, register, documents, T, Link);
			return text.ToString();
		}

		// a folder: all of the catalog, or a folder of it
		if (!FxFiles.DirectoryExists(scope) || !(Same(scope, catalog.Folder) || catalog.Contains(scope)))
		{
			problem = new($"{InvalidScope}.folder", generation.Scope);
			return null;
		}
		var inside = documents.Where(d => d.Type.Name != "view" && (Same(scope, catalog.Folder) || d.File.Markdown!.StartsWith(Path.TrimEndingDirectorySeparator(scope) + Path.DirectorySeparatorChar,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))).OrderBy(d => d.File.Path, StringComparer.Ordinal).ToList();
		if (generation.Recipe == Status)
			Dashboard(text, inside, T, Link);
		else
		{
			var tag = generation.Recipe.Length > Catalog.Length ? generation.Recipe[(Catalog.Length + 1)..] : null;
			Table(text, inside.Where(d => tag is null || d.Sidecar.Tags.Contains(tag)).ToList(), T, Link);
		}
		return text.ToString();
	}

	static bool Same(string a, string b)
		=> string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	/// <summary>Text for a cell or a link of Markdown: its pipes and brackets escaped, on one line.</summary>
	static string Escape(string text) => text.Replace("\\", "\\\\").Replace("|", "\\|").Replace("[", "\\[").Replace("]", "\\]").Replace('\n', ' ').Replace('\r', ' ');

	static string Date(string instant) => instant[..10];

	static void Dashboard(StringBuilder text, IReadOnlyList<DocDocument> documents, Func<string, object?[], string> t, Func<string, string, string> link)
	{
		string T(string key, params object?[] args) => t(key, args);
		var attention = documents.Where(d => d.Type.Attention.Contains(d.Sidecar.Status)).ToList();
		var open = documents.Where(d => d.Class == DocStatusClass.Open && !attention.Contains(d)).ToList();
		var current = documents.Where(d => d.Class == DocStatusClass.Current && !attention.Contains(d)).ToList();
		text.Append($"\n{T("docs.view.status.summary", open.Count, current.Count, attention.Count, documents.Count)}\n");
		void Section(string title, IReadOnlyList<DocDocument> items)
		{
			text.Append($"\n## {T(title)}\n");
			if (items.Count == 0)
			{
				text.Append($"\n{T("docs.view.none")}\n");
				return;
			}
			foreach (var type in DocTypes.All.Where(type => items.Any(d => d.Type == type)))
			{
				text.Append($"\n### {type.Name}\n\n");
				foreach (var d in items.Where(d => d.Type == type))
					text.Append($"- {link(d.Sidecar.Title, d.File.Markdown!)} · `{d.Sidecar.Status}` · {Date(d.Sidecar.UpdatedAt)}\n");
			}
		}
		Section("docs.view.status.attention", attention);
		Section("docs.view.status.open", open);
		Section("docs.view.status.current", current);
	}

	static void Table(StringBuilder text, IReadOnlyList<DocDocument> documents, Func<string, object?[], string> t, Func<string, string, string> link)
	{
		string T(string key, params object?[] args) => t(key, args);
		if (documents.Count == 0)
		{
			text.Append($"\n{T("docs.view.none")}\n");
			return;
		}
		text.Append($"\n| {T("docs.view.column.document")} | {T("docs.view.column.type")} | {T("docs.view.column.status")} | {T("docs.view.column.updated")} |\n|---|---|---|---|\n");
		foreach (var d in documents)
			text.Append($"| {link(d.Sidecar.Title, d.File.Markdown!)} | {d.Type.Name} | `{d.Sidecar.Status}` | {Date(d.Sidecar.UpdatedAt)} |\n");
	}

	/// <summary>The order of the entries: by the number of their key (<c>I-2</c> before <c>I-10</c>), then by their name.</summary>
	static (string Prefix, long Number, string Name) Order(DocEntry entry)
	{
		if (entry.Key?.LastIndexOf('-') is > 0 and var dash && long.TryParse(entry.Key[(dash + 1)..], CultureInfo.InvariantCulture, out var number))
			return (entry.Key[..dash], number, entry.Name);
		return ("~", long.MaxValue, entry.Name);
	}

	static string Value(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.String => value.GetString()!,
		JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Value)),
		_ => value.GetRawText()
	};

	static void Backlogs(StringBuilder text, DocDocument register, IReadOnlyList<DocDocument> documents, Func<string, object?[], string> t, Func<string, string, string> link)
	{
		string T(string key, params object?[] args) => t(key, args);
		var entries = register.Sidecar.Entries
			.OrderBy(e => Order(e).Prefix, StringComparer.Ordinal).ThenBy(e => Order(e).Number).ThenBy(e => e.Name, StringComparer.Ordinal)
			.ToList();
		var open = entries.Where(e => e.Open).ToList();
		var closed = entries.Where(e => !e.Open).ToList();
		var byId = documents.GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		string Documents(DocEntry entry)
			=> string.Join(", ", entry.Relations.Select(r => byId.GetValueOrDefault(r.TargetId)).OfType<DocDocument>().Select(d => link(d.Sidecar.Title, d.File.Markdown!)));
		string Fields(DocEntry entry) => Escape(string.Join(" · ", entry.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {Value(f.Value)}")));
		text.Append($"\n{T("docs.view.backlog.summary", open.Count, closed.Count)}\n");
		foreach (var status in open.Select(e => e.Status).Distinct().Order(StringComparer.Ordinal))
		{
			text.Append($"\n## {status}\n\n| {T("docs.view.column.entry")} | {T("docs.view.column.title")} | {T("docs.view.column.fields")} | {T("docs.view.column.documents")} |\n|---|---|---|---|\n");
			foreach (var e in open.Where(e => e.Status == status))
				text.Append($"| {Escape(e.Key ?? e.Name)} | {Escape(e.Title)} | {Fields(e)} | {Documents(e)} |\n");
		}
		if (closed.Count == 0)
			return;
		text.Append($"\n## {T("docs.view.backlog.closed")}\n\n| {T("docs.view.column.entry")} | {T("docs.view.column.title")} | {T("docs.view.column.status")} | {T("docs.view.column.closed")} |\n|---|---|---|---|\n");
		foreach (var e in closed)
			text.Append($"| {Escape(e.Key ?? e.Name)} | {Escape(e.Title)} | `{e.Status}` | {Date(e.ClosedAt!)} |\n");
	}
}
