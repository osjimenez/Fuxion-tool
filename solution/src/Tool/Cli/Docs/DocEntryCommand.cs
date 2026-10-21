using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli.Mounts;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Results;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace Fuxion.Tool.Cli.Docs;

/// <summary>
/// <c>fx doc entry add|set|close|list</c> (plan U, decision 18): the entries of a register, which live in its sidecar
/// and change only through these commands; each change finalizes the register.
/// </summary>
public static class DocEntryCommand
{
	public const string ListSchema = "fx-doc-entries/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("entry", Texts.Get("doc.entry"));
		Option<bool> DryRun() => new("--dry-run") { Description = Texts.Get("doc.dry-run") };
		Argument<string> Register() => new("register") { Description = Texts.Get("doc.entry.register") };
		Argument<string> Entry() => new("entry") { Description = Texts.Get("doc.entry.entry") };
		Option<string?> Text(string name, string description, string help) => new(name) { Description = Texts.Get(description), HelpName = help };
		Option<string[]> Many(string name, string description, string help) => new(name) { Description = Texts.Get(description), HelpName = help };

		var addRegister = Register();
		var title = new Argument<string>("title") { Description = Texts.Get("doc.entry.title") };
		var name = Text("--name", "doc.entry.name", "name");
		var status = Text("--status", "doc.entry.status", "status");
		var summary = Text("--summary", "doc.entry.summary", "text");
		var key = Text("--key", "doc.entry.key", "key");
		var created = Text("--created", "doc.entry.created", "date");
		var tags = Many("--tag", "doc.entry.tag", "tag");
		var fields = Many("--field", "doc.entry.field", "name=value");
		var relations = Many("--relation", "doc.entry.relation", "kind=target");
		var addDryRun = DryRun();
		var add = new Command("add", Texts.Get("doc.entry.add"))
		{
			addRegister, title, name, status, summary, key, created, tags, fields, relations, addDryRun
		}.Describe(DocCommand.EditSchema, writes: true);
		add.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			if (Edit(console, result.GetValue(fields), result.GetValue(relations), []) is not { } edit)
				return FxConsole.UsageError;
			edit = edit with
			{
				Name = result.GetValue(name),
				Status = result.GetValue(status),
				Summary = result.GetValue(summary),
				Key = result.GetValue(key),
				Created = result.GetValue(created),
				Tags = result.GetValue(tags) ?? []
			};
			return console.Run(_ => DocCommand.Write(console, result.GetValue(addDryRun), (catalog, display) =>
				DocCommand.One(console, catalog, result.GetValue(addRegister)!) is { } register
					? DocEditor.AddEntry(catalog, display, console.Settings.Directory, register, result.GetValue(title)!, edit, DateTimeOffset.UtcNow)
					: null));
		});
		command.Add(add);

		var setRegister = Register();
		var setEntry = Entry();
		var setTitle = Text("--title", "doc.entry.new-title", "title");
		var setStatus = Text("--status", "doc.entry.status", "status");
		var setSummary = Text("--summary", "doc.entry.summary", "text");
		var setTags = Many("--tag", "doc.entry.tag", "tag");
		var setFields = Many("--field", "doc.entry.set-field", "name=value");
		var setRelations = Many("--relation", "doc.entry.relation", "kind=target");
		var unrelate = Many("--unrelate", "doc.entry.unrelate", "kind=target");
		var setDryRun = DryRun();
		var set = new Command("set", Texts.Get("doc.entry.set"))
		{
			setRegister, setEntry, setTitle, setStatus, setSummary, setTags, setFields, setRelations, unrelate, setDryRun
		}.Describe(DocCommand.EditSchema, writes: true);
		set.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			if (Edit(console, result.GetValue(setFields), result.GetValue(setRelations), result.GetValue(unrelate)) is not { } edit)
				return FxConsole.UsageError;
			edit = edit with
			{
				Title = result.GetValue(setTitle),
				Status = result.GetValue(setStatus),
				Summary = result.GetValue(setSummary),
				Tags = result.GetValue(setTags) ?? []
			};
			return console.Run(_ => DocCommand.Write(console, result.GetValue(setDryRun), (catalog, display) =>
				DocCommand.One(console, catalog, result.GetValue(setRegister)!) is { } register
					? DocEditor.SetEntry(catalog, display, console.Settings.Directory, register, result.GetValue(setEntry)!, edit, DateTimeOffset.UtcNow)
					: null));
		});
		command.Add(set);

		var closeRegister = Register();
		var closeEntry = Entry();
		var closeStatus = Text("--status", "doc.entry.close-status", "status");
		var closeDryRun = DryRun();
		var close = new Command("close", Texts.Get("doc.entry.close")) { closeRegister, closeEntry, closeStatus, closeDryRun }.Describe(DocCommand.EditSchema, writes: true);
		close.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => DocCommand.Write(console, result.GetValue(closeDryRun), (catalog, display) =>
				DocCommand.One(console, catalog, result.GetValue(closeRegister)!) is { } register
					? DocEditor.CloseEntry(catalog, display, register, result.GetValue(closeEntry)!, result.GetValue(closeStatus), DateTimeOffset.UtcNow)
					: null));
		});
		command.Add(close);

		var listRegister = Register();
		var open = new Option<bool>("--open") { Description = Texts.Get("doc.entry.open") };
		var closed = new Option<bool>("--closed") { Description = Texts.Get("doc.entry.closed") };
		var list = new Command("list", Texts.Get("doc.entry.list")) { listRegister, open, closed }.Describe(ListSchema);
		list.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => List(console, result.GetValue(listRegister)!, result.GetValue(open), result.GetValue(closed)));
		});
		command.Add(list);
		return command;
	}

	/// <summary>
	/// The fields and relations of a change: <c>name=value</c> (a JSON value, or text; empty, the field goes) and
	/// <c>kind=target</c>. Null, said on stderr, if one has no <c>=</c>.
	/// </summary>
	static DocEntryEdit? Edit(FxConsole console, string[]? fields, string[]? relations, string[]? unrelate)
	{
		var edit = new DocEntryEdit();
		var parsedFields = new List<KeyValuePair<string, JsonElement?>>();
		foreach (var field in fields ?? [])
		{
			if (field.IndexOf('=') is not (> 0 and var at))
				return Wrong(field);
			var value = field[(at + 1)..];
			parsedFields.Add(KeyValuePair.Create(field[..at].Trim(), value.Length == 0 ? null : (JsonElement?)Value(value)));
		}
		List<(string, string)>? Pairs(string[]? items)
		{
			var pairs = new List<(string, string)>();
			foreach (var item in items ?? [])
				if (item.IndexOf('=') is > 0 and var at)
					pairs.Add((item[..at].Trim(), item[(at + 1)..].Trim()));
				else
				{
					Wrong(item);
					return null;
				}
			return pairs;
		}
		if (Pairs(relations) is not { } added || Pairs(unrelate) is not { } removed)
			return null;
		return edit with { Fields = parsedFields, Relations = added, Unrelate = removed };

		DocEntryEdit? Wrong(string item)
		{
			console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.entry.pair", item))}[/]");
			return null;
		}
	}

	/// <summary>A value of a field: JSON if it is (a number, true, a list, a quoted text), text if not.</summary>
	static JsonElement Value(string value)
	{
		try
		{
			using var json = JsonDocument.Parse(value);
			return json.RootElement.Clone();
		}
		catch (JsonException)
		{
			using var text = JsonDocument.Parse(JsonSerializer.Serialize(value, FxJsonContext.Default.String));
			return text.RootElement.Clone();
		}
	}

	static int List(FxConsole console, string selector, bool open, bool closed)
	{
		var diagnostics = new List<FxDiagnostic>();
		void Json(string? register, IReadOnlyList<JsonEntry> entries)
			=> console.WriteJson(new DocEntriesDocument(ListSchema, FxConsole.Ok(diagnostics), register, entries, JsonDiagnostic.From(diagnostics)), c => c.DocEntriesDocument);
		if (DocsModule.Find(console.Settings.Directory, diagnostics) is not { } catalog)
		{
			if (console.Settings.IsMachine)
				Json(null, []);
			return console.Report(diagnostics);
		}
		if (DocCommand.One(console, catalog, selector) is not { } file)
			return FxConsole.UsageError;
		var documents = DocChecker.Read(catalog);
		if (documents.FirstOrDefault(d => d.File == file) is not { Type.Name: "register" } register)
		{
			diagnostics.Add(FxDiagnostic.Error(DocEditor.NotARegister, new(DocEditor.NotARegister, file.Path, documents.FirstOrDefault(d => d.File == file)?.Type.Name ?? "-")));
			if (console.Settings.IsMachine)
				Json(file.Path, []);
			return console.Report(diagnostics);
		}
		var byId = documents.GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var entries = documents.SelectMany(d => d.Sidecar.Entries).GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var shown = register.Sidecar.Entries.Where(e => open == closed || open == e.Open).ToList();
		if (console.Settings.IsMachine)
		{
			Json(register.File.Path, shown.Select(e => new JsonEntry(e.Id, e.Name, e.Key, e.Title, e.Status, e.Open, e.Summary, e.Tags.Order(StringComparer.Ordinal).ToList(),
				e.CreatedAt, e.ClosedAt, e.Relations.Select(r => byId.GetValueOrDefault(r.TargetId) is { } target
						? new JsonDocRelation(r.Kind, r.TargetId, target.File.Path, target.Sidecar.Title)
						: entries.GetValueOrDefault(r.TargetId) is { } entry
							? new JsonDocRelation(r.Kind, r.TargetId, null, entry.Key is null ? entry.Title : $"{entry.Key} {entry.Title}")
							: new JsonDocRelation(r.Kind, r.TargetId, null, null))
					.ToList(),
				e.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal))).ToList());
			return 0;
		}
		if (shown.Count == 0)
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.entry.none"))}[/]");
		else
		{
			var table = MountCommand.Table(console, Texts.Get("doc.entry.column.entry"), Texts.Get("doc.column.status"), Texts.Get("doc.column.title"));
			foreach (var e in shown)
				table.AddRow(
					MountCommand.Cell(console, e.Key ?? e.Name),
					MountCommand.Cell(console, e.Status, e.Open ? null : "gray"),
					MountCommand.Cell(console, e.Title, e.Open ? null : "gray"));
			console.Write(table);
		}
		return 0;
	}
}
