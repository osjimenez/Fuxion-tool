using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Fuxion.Tool.Cli.Mounts;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace Fuxion.Tool.Cli.Docs;

/// <summary>
/// <c>fx doc</c> (plan U; the SPEC of the module docs, 11.1): the catalog of documents of the space. <c>check</c>,
/// <c>list</c>, <c>show</c> and <c>schema</c> read it; <c>new</c>, <c>finalize</c>, <c>status</c> and <c>relate</c> write
/// in it, all or nothing.
/// </summary>
public static class DocCommand
{
	public const string EditSchema = "fx-doc-edit/1";
	public const string CheckSchema = "fx-doc-check/1";
	public const string ListSchema = "fx-doc-list/1";
	public const string ShowSchema = "fx-doc-show/1";
	public const string SchemasSchema = "fx-doc-schemas/1";

	static readonly string[] Classes = [.. Enum.GetValues<DocStatusClass>().Select(ClassName)];

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("doc", Texts.Get("doc"));

		var documents = new Argument<string[]>("documents") { Description = Texts.Get("doc.check.documents"), Arity = ArgumentArity.ZeroOrMore };
		var branches = new Option<bool>("--branches") { Description = Texts.Get("doc.check.branches") };
		var fetch = new Option<bool>("--fetch") { Description = Texts.Get("doc.check.fetch") };
		var check = new Command("check", Texts.Get("doc.check")) { documents, branches, fetch }.Describe(CheckSchema, network: true);
		check.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Check(console, result.GetValue(documents) ?? [], result.GetValue(branches) || result.GetValue(fetch), result.GetValue(fetch)));
		});
		command.Add(check);

		var type = new Option<string?>("--type") { Description = Texts.Get("doc.list.type"), HelpName = "type" };
		type.AcceptOnlyFromAmong([.. DocTypes.All.Select(t => t.Name)]);
		var profile = new Option<string?>("--profile") { Description = Texts.Get("doc.list.profile"), HelpName = "profile" };
		var status = new Option<string?>("--status") { Description = Texts.Get("doc.list.status"), HelpName = "status" };
		var @class = new Option<string?>("--class") { Description = Texts.Get("doc.list.class"), HelpName = "class" };
		@class.AcceptOnlyFromAmong(Classes);
		var tag = new Option<string?>("--tag") { Description = Texts.Get("doc.list.tag"), HelpName = "tag" };
		var relation = new Option<string?>("--relation") { Description = Texts.Get("doc.list.relation"), HelpName = "kind" };
		relation.AcceptOnlyFromAmong([.. DocRelations.Kinds]);
		var list = new Command("list", Texts.Get("doc.list")) { type, profile, status, @class, tag, relation }.Describe(ListSchema);
		list.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			bool Filter(DocDocument d)
				=> (result.GetValue(type) is not { } t || d.Sidecar.Type == t)
				   && (result.GetValue(profile) is not { } p || d.Sidecar.Profile == p)
				   && (result.GetValue(status) is not { } s || d.Sidecar.Status == s)
				   && (result.GetValue(@class) is not { } c || ClassName(d.Class) == c)
				   && (result.GetValue(tag) is not { } g || d.Sidecar.Tags.Contains(g))
				   && (result.GetValue(relation) is not { } r || d.Sidecar.Relations.Any(x => x.Kind == r));
			return console.Run(_ => List(console, Filter));
		});
		command.Add(list);

		var document = new Argument<string>("document") { Description = Texts.Get("doc.show.document") };
		var show = new Command("show", Texts.Get("doc.show")) { document }.Describe(ShowSchema);
		show.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Show(console, result.GetValue(document)!));
		});
		command.Add(show);

		var schemaType = new Argument<string?>("type") { Description = Texts.Get("doc.schema.type"), Arity = ArgumentArity.ZeroOrOne };
		schemaType.AcceptOnlyFromAmong([.. DocTypes.All.Select(t => t.Name)]);
		var schema = new Command("schema", Texts.Get("doc.schema")) { schemaType }.Describe(SchemasSchema);
		schema.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return Schema(console, result.GetValue(schemaType));
		});
		command.Add(schema);
		AddWriters(command, global, currentDirectory);
		command.Add(DocEntryCommand.Create(global, currentDirectory));
		command.Add(DocKeyCommand.Create(global, currentDirectory));
		return command;
	}

	/// <summary>The commands that write (the SPEC of the module, 11.1): each one all or nothing, with <c>--dry-run</c>.</summary>
	static void AddWriters(Command command, GlobalOptions global, string currentDirectory)
	{
		Option<bool> DryRun() => new("--dry-run") { Description = Texts.Get("doc.dry-run") };

		var newType = new Argument<string>("type") { Description = Texts.Get("doc.new.type") };
		newType.AcceptOnlyFromAmong([.. DocTypes.All.Select(t => t.Name)]);
		var title = new Argument<string>("title") { Description = Texts.Get("doc.new.title") };
		var slug = new Option<string?>("--slug") { Description = Texts.Get("doc.new.slug"), HelpName = "slug" };
		var profile = new Option<string?>("--profile") { Description = Texts.Get("doc.new.profile"), HelpName = "profile" };
		var path = new Option<string?>("--path") { Description = Texts.Get("doc.new.path"), HelpName = "folder" };
		var tags = new Option<string[]>("--tag") { Description = Texts.Get("doc.new.tag"), HelpName = "tag" };
		var actor = new Option<string?>("--actor") { Description = Texts.Get("doc.new.actor", DocEditor.ActorVariable), HelpName = "actor" };
		var recipe = new Option<string?>("--recipe") { Description = Texts.Get("doc.new.recipe"), HelpName = "recipe" };
		var scope = new Option<string?>("--scope") { Description = Texts.Get("doc.new.scope"), HelpName = "path" };
		var newDryRun = DryRun();
		var @new = new Command("new", Texts.Get("doc.new")) { newType, title, slug, profile, path, tags, actor, recipe, scope, newDryRun }.Describe(EditSchema, writes: true);
		@new.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Write(console, result.GetValue(newDryRun), (catalog, display) => DocEditor.New(catalog, display, console.Settings.Directory,
				result.GetValue(newType)!, result.GetValue(title)!, result.GetValue(slug), result.GetValue(profile), result.GetValue(path), result.GetValue(tags) ?? [],
				result.GetValue(actor) ?? Environment.GetEnvironmentVariable(DocEditor.ActorVariable), DateTimeOffset.UtcNow,
				result.GetValue(recipe) is { } r ? new DocGeneration(r, result.GetValue(scope) ?? catalog.Config.Catalog) : null)));
		});
		command.Add(@new);

		var documents = new Argument<string[]>("documents") { Description = Texts.Get("doc.finalize.documents"), Arity = ArgumentArity.ZeroOrMore };
		var correction = new Option<string?>("--correction") { Description = Texts.Get("doc.finalize.correction"), HelpName = "note" };
		var finalizeDryRun = DryRun();
		var finalize = new Command("finalize", Texts.Get("doc.finalize")) { documents, correction, finalizeDryRun }.Describe(EditSchema, writes: true);
		finalize.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			var selectors = result.GetValue(documents) ?? [];
			return console.Run(_ => Write(console, result.GetValue(finalizeDryRun), (catalog, display) =>
				selectors.Length == 0
					? DocEditor.Finalize(catalog, display, null, result.GetValue(correction), DateTimeOffset.UtcNow)
					: Select(console, catalog, selectors) is { } files
						? DocEditor.Finalize(catalog, display, files, result.GetValue(correction), DateTimeOffset.UtcNow)
						: null));
		});
		command.Add(finalize);

		var statusDocument = new Argument<string>("document") { Description = Texts.Get("doc.show.document") };
		var status = new Argument<string>("status") { Description = Texts.Get("doc.status.status") };
		var statusDryRun = DryRun();
		var setStatus = new Command("status", Texts.Get("doc.status")) { statusDocument, status, statusDryRun }.Describe(EditSchema, writes: true);
		setStatus.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Write(console, result.GetValue(statusDryRun), (catalog, display) =>
				One(console, catalog, result.GetValue(statusDocument)!) is { } file
					? DocEditor.SetStatus(catalog, display, file, result.GetValue(status)!, DateTimeOffset.UtcNow)
					: null));
		});
		command.Add(setStatus);

		var source = new Argument<string>("document") { Description = Texts.Get("doc.show.document") };
		var kind = new Argument<string>("kind") { Description = Texts.Get("doc.relate.kind") };
		kind.AcceptOnlyFromAmong([.. DocRelations.Kinds]);
		var target = new Argument<string>("target") { Description = Texts.Get("doc.relate.target") };
		var remove = new Option<bool>("--remove") { Description = Texts.Get("doc.relate.remove") };
		var relateDryRun = DryRun();
		var relate = new Command("relate", Texts.Get("doc.relate")) { source, kind, target, remove, relateDryRun }.Describe(EditSchema, writes: true);
		relate.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Write(console, result.GetValue(relateDryRun), (catalog, display) =>
			{
				if (One(console, catalog, result.GetValue(source)!) is not { } from)
					return null;
				// the target, an entry of a register (its key, its identity), or a document
				var to = result.GetValue(target)!;
				if (DocEditor.EntryTarget(catalog, to) is { } entry)
					return DocEditor.RelateToEntry(catalog, display, from, result.GetValue(kind)!, entry, result.GetValue(remove), DateTimeOffset.UtcNow);
				return One(console, catalog, to) is { } document
					? DocEditor.Relate(catalog, display, from, result.GetValue(kind)!, document, result.GetValue(remove), DateTimeOffset.UtcNow)
					: null;
			}));
		});
		command.Add(relate);

		var adoptDocuments = new Argument<string[]>("documents") { Description = Texts.Get("doc.adopt.documents"), Arity = ArgumentArity.ZeroOrMore };
		var adoptType = new Option<string?>("--type") { Description = Texts.Get("doc.adopt.type"), HelpName = "type" };
		adoptType.AcceptOnlyFromAmong([.. DocTypes.All.Select(t => t.Name)]);
		var adoptProfile = new Option<string?>("--profile") { Description = Texts.Get("doc.new.profile"), HelpName = "profile" };
		var adoptStatus = new Option<string?>("--status") { Description = Texts.Get("doc.adopt.status"), HelpName = "status" };
		var created = new Option<string?>("--created") { Description = Texts.Get("doc.adopt.created"), HelpName = "date" };
		var manifests = new Option<string[]>("--manifest") { Description = Texts.Get("doc.adopt.manifest"), HelpName = "file" };
		var adoptActor = new Option<string?>("--actor") { Description = Texts.Get("doc.new.actor", DocEditor.ActorVariable), HelpName = "actor" };
		var adoptDryRun = DryRun();
		var adopt = new Command("adopt", Texts.Get("doc.adopt"))
		{
			adoptDocuments, adoptType, adoptProfile, adoptStatus, created, manifests, adoptActor, adoptDryRun
		}.Describe(EditSchema, writes: true);
		adopt.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			var selectors = result.GetValue(adoptDocuments) ?? [];
			var files = result.GetValue(manifests) ?? [];
			var actor = result.GetValue(adoptActor) ?? Environment.GetEnvironmentVariable(DocEditor.ActorVariable);
			if (files.Length > 0 && (selectors.Length > 0 || result.GetValue(adoptType) is not null || result.GetValue(adoptProfile) is not null
			                         || result.GetValue(adoptStatus) is not null || result.GetValue(created) is not null))
			{
				console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.adopt.manifest-alone"))}[/]");
				return FxConsole.UsageError;
			}
			return console.Run(_ => Write(console, result.GetValue(adoptDryRun), (catalog, display) =>
			{
				if (files.Length > 0)
				{
					var change = new DocChange(catalog, display);
					var targets = new List<DocManifestTarget>();
					foreach (var file in files.Select(f => FxLayout.Full(console.Settings.Directory, f)))
					{
						if (!FxFiles.Exists(file))
						{
							console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.adopt.no-manifest", file))}[/]");
							return null;
						}
						if (DocAdoption.Read(file, display(file), change.Diagnostics) is { } read)
							targets.AddRange(read);
					}
					return change.Ok ? DocAdoption.Apply(catalog, display, targets, actor, DateTimeOffset.UtcNow) : change;
				}
				List<DocFile>? chosen = null;
				if (selectors.Length > 0 && (chosen = Select(console, catalog, selectors)) is null)
					return null;
				return DocAdoption.Bootstrap(catalog, display, chosen, result.GetValue(adoptType), result.GetValue(adoptProfile), result.GetValue(adoptStatus),
					result.GetValue(created), actor, DateTimeOffset.UtcNow);
			}));
		});
		command.Add(adopt);

		var moveDocument = new Argument<string>("document") { Description = Texts.Get("doc.show.document") };
		var folder = new Argument<string>("folder") { Description = Texts.Get("doc.move.folder") };
		var moveDryRun = DryRun();
		var move = new Command("move", Texts.Get("doc.move")) { moveDocument, folder, moveDryRun }.Describe(EditSchema, writes: true);
		move.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Write(console, result.GetValue(moveDryRun), (catalog, display) =>
				One(console, catalog, result.GetValue(moveDocument)!) is { } file
					? DocEditor.Move(catalog, display, file, result.GetValue(folder)!, console.Settings.Directory)
					: null));
		});
		command.Add(move);
	}

	/// <summary>The one document <paramref name="selector"/> names; null (said on stderr) if it names none or several.</summary>
	internal static DocFile? One(FxConsole console, DocCatalog catalog, string selector)
	{
		if (Select(console, catalog, [selector]) is not { } files)
			return null;
		if (files.Count == 1)
			return files[0];
		console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.many-documents", selector, files.Count))}[/]");
		return null;
	}

	/// <summary>
	/// A change of the catalog: written (unless <c>--dry-run</c> or with errors: all or nothing) and shown, the diff of
	/// each file or why nothing changes. <paramref name="work"/> gives null if what it names is not there (a usage error,
	/// said already).
	/// </summary>
	internal static int Write(FxConsole console, bool dryRun, Func<DocCatalog, Func<string, string>, DocChange?> work)
	{
		var found = new List<FxDiagnostic>();
		void Json(DocChange? change, bool written)
		{
			var diagnostics = change is null ? found : [.. found, .. change.Diagnostics];
			var ok = FxConsole.Ok(diagnostics);
			var shown = ok && change is not null;
			console.WriteJson(new DocEditDocument(EditSchema, ok, ok && change is { Changed: true }, written, change?.Documents ?? [],
				shown ? change!.Files.Select(f => new JsonFileEdit(f.File, Core.Text.LineDiff.Unified(f.Diff))).ToList() : [],
				shown ? change!.Moved.Select(m => new JsonFileMove(m.From, m.To)).ToList() : [], shown ? change!.Removed : [], JsonDiagnostic.From(diagnostics)),
				c => c.DocEditDocument);
		}
		if (DocsModule.Find(console.Settings.Directory, found) is not { } catalog)
		{
			if (console.Settings.IsMachine)
				Json(null, false);
			return console.Report(found);
		}
		if (work(catalog, Display(catalog)) is not { } change)
			return FxConsole.UsageError;
		var written = !dryRun && change.Write();
		if (console.Settings.IsMachine)
		{
			Json(change, written);
			return FxConsole.ExitCode(change.Diagnostics);
		}
		if (change.Ok && !change.Changed)
		{
			var reasons = change.Diagnostics.Where(d => d.Severity == FxSeverity.Info).Select(d => d.Message.ToString()).ToList();
			console.Line($"[gray]{FxConsole.Escape(reasons.Count > 0 ? string.Join(" ", reasons) : Texts.Get("doc.edit.unchanged"))}[/]");
		}
		else if (change.Ok)
		{
			foreach (var move in change.Moved)
				console.Line($"[yellow]{FxConsole.Escape(Texts.Get("doc.edit.moved", move.From, move.To))}[/]");
			foreach (var removed in change.Removed)
				console.Line($"[red]{FxConsole.Escape(Texts.Get("doc.edit.removed", removed))}[/]");
			foreach (var file in change.Files)
				ManifestCommands.WriteDiff(console, file.File, file.Diff);
			console.Line(written
				? $"[green]✓[/] {FxConsole.Escape(Texts.Get("doc.edit.written", string.Join(", ", change.Documents)))}"
				: $"[yellow]{FxConsole.Escape(Texts.Get("doc.edit.dry-run"))}[/]");
		}
		return console.Report(change.Diagnostics.Where(d => d.Severity != FxSeverity.Info).ToList());
	}

	static string ClassName(DocStatusClass c) => c.ToString().ToLowerInvariant();

	/// <summary>The files of the diagnostics: relative to the space of the catalog.</summary>
	internal static Func<string, string> Display(DocCatalog catalog) => path => FxLayout.Relative(catalog.Root, path);

	static string CatalogPath(DocCatalog catalog) => FxLayout.Relative(catalog.Root, catalog.Folder);

	static JsonDoc Doc(DocDocument d) => new(d.File.Path, d.Sidecar.Id, d.Sidecar.Type, d.Sidecar.Profile, d.Sidecar.Status, ClassName(d.Class), d.Sidecar.Title,
		d.Sidecar.Tags.Order(StringComparer.Ordinal).ToList(), d.Sidecar.CreatedAt, d.Sidecar.UpdatedAt, d.Sidecar.CreatedBy);

	/// <summary>
	/// The documents the selectors name, each with what it names; null (said on stderr) if one names none: a usage error
	/// (the SPEC of the module, 14).
	/// </summary>
	static List<DocFile>? Select(FxConsole console, DocCatalog catalog, IEnumerable<string> selectors)
	{
		IReadOnlyDictionary<string, DocFile>? ids = null;
		var files = new List<DocFile>();
		foreach (var selector in selectors)
		{
			if (Guid.TryParse(selector, out _))
				ids ??= DocChecker.Read(catalog).GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => g.First().File, StringComparer.OrdinalIgnoreCase);
			var named = catalog.Select(selector, console.Settings.Directory, ids);
			if (named.Count == 0)
			{
				console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.unknown-document", selector, CatalogPath(catalog)))}[/]");
				return null;
			}
			files.AddRange(named.Where(f => !files.Contains(f)));
		}
		return files;
	}

	static int Check(FxConsole console, IReadOnlyList<string> selectors, bool branches = false, bool fetch = false)
	{
		var diagnostics = new List<FxDiagnostic>();
		void Json(DocCatalog? catalog, int count, IReadOnlyList<string> withProblems)
			=> console.WriteJson(new DocCheckDocument(CheckSchema, FxConsole.Ok(diagnostics), catalog is null ? null : CatalogPath(catalog), count, withProblems,
				JsonDiagnostic.From(diagnostics)), c => c.DocCheckDocument);
		if (DocsModule.Find(console.Settings.Directory, diagnostics) is not { } catalog)
		{
			if (console.Settings.IsMachine)
				Json(null, 0, []);
			return console.Report(diagnostics);
		}
		List<DocFile>? only = null;
		if (selectors.Count > 0 && (only = Select(console, catalog, selectors)) is null)
			return FxConsole.UsageError;
		var display = Display(catalog);
		diagnostics.AddRange(DocChecker.Check(catalog, display, only).Diagnostics);
		// against the remote branches git knows (--fetch: brought first)
		if (branches)
		{
			if (fetch && DocBranches.Fetch(catalog) is { } failed)
				diagnostics.Add(failed);
			var mine = DocChecker.Read(catalog).Where(d => only is null || only.Contains(d.File)).ToList();
			diagnostics.AddRange(DocBranches.Collisions(catalog, mine, DocBranches.Read(catalog), display));
		}
		var files = (IReadOnlyList<DocFile>?)only ?? catalog.Files;
		bool Of(DocFile file, FxDiagnostic d)
			=> d.File is not null && (file.Markdown is not null && d.File == display(file.Markdown) || file.Sidecar is not null && d.File == display(file.Sidecar));
		var withProblems = files.Where(f => diagnostics.Any(d => d.Severity != FxSeverity.Info && Of(f, d))).ToList();
		if (console.Settings.IsMachine)
		{
			Json(catalog, files.Count, withProblems.Select(f => f.Path).ToList());
			return FxConsole.ExitCode(diagnostics);
		}

		// for people: a line per document with problems, its problems below, and a summary (the SPEC of the module, 11.4)
		var shown = diagnostics.Where(d => d.Severity != FxSeverity.Info || console.Settings.Verbose).ToList();
		void Problems(IEnumerable<FxDiagnostic> own)
		{
			foreach (var d in own.OrderByDescending(d => d.Severity).ThenBy(d => d.Line))
			{
				var glyph = d.Severity switch { FxSeverity.Error => "[red]✗[/]", FxSeverity.Warning => "[yellow]![/]", _ => "[gray]i[/]" };
				var where = d.File is null ? "" : d.Line is null ? $"{d.File} · " : $"{d.File}:{d.Line} · ";
				console.Line($"    {glyph} {FxConsole.Escape(d.Message.ToString())} [gray]({FxConsole.Escape(where)}{FxConsole.Escape(d.Code)})[/]");
				if (d.Fix is not null)
					console.Line($"      [cyan]→[/]{FxConsole.Escape(FxConsole.Fix(d.Fix)[2..])}");
			}
		}
		foreach (var file in files)
		{
			var own = shown.Where(d => Of(file, d)).ToList();
			if (own.Count == 0)
				continue;
			var mark = own.Any(d => d.Severity == FxSeverity.Error) ? "[red]✗[/]" : own.Any(d => d.Severity == FxSeverity.Warning) ? "[yellow]![/]" : "[gray]i[/]";
			console.Line($"{mark} {FxConsole.Escape(file.Path)}");
			Problems(own);
		}
		var rest = shown.Where(d => !files.Any(f => Of(f, d))).ToList();
		if (rest.Count > 0)
		{
			console.Line($"{(rest.Any(d => d.Severity == FxSeverity.Error) ? "[red]✗[/]" : "[yellow]![/]")} {FxConsole.Escape(Texts.Get("doc.check.catalog", CatalogPath(catalog)))}");
			Problems(rest);
		}
		console.Line(withProblems.Count == 0
			? $"[green]✓[/] {FxConsole.Escape(Texts.Get("doc.check.all-valid", files.Count))}"
			: $"[yellow]{FxConsole.Escape(Texts.Get("doc.check.problems", files.Count, withProblems.Count))}[/]");
		var pending = diagnostics.Count(d => d.Code == DocChecker.BootstrapPending);
		if (pending > 0)
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.check.bootstrap", pending))}[/]");
		return console.Report(diagnostics, write: false);
	}

	static int List(FxConsole console, Func<DocDocument, bool> filter)
	{
		var diagnostics = new List<FxDiagnostic>();
		var catalog = DocsModule.Find(console.Settings.Directory, diagnostics);
		var documents = catalog is null ? [] : DocChecker.Read(catalog).Where(filter).OrderBy(d => d.File.Path, StringComparer.Ordinal).ToList();
		if (console.Settings.IsMachine)
		{
			console.WriteJson(new DocListDocument(ListSchema, FxConsole.Ok(diagnostics), catalog is null ? null : CatalogPath(catalog), documents.Select(Doc).ToList(),
				JsonDiagnostic.From(diagnostics)), c => c.DocListDocument);
			return FxConsole.ExitCode(diagnostics);
		}
		if (catalog is null)
			return console.Report(diagnostics);
		if (documents.Count == 0)
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.list.none"))}[/]");
		else
		{
			var table = MountCommand.Table(console, Texts.Get("doc.column.type"), Texts.Get("doc.column.status"), Texts.Get("doc.column.title"), Texts.Get("doc.column.path"));
			foreach (var d in documents)
				table.AddRow(
					MountCommand.Cell(console, d.Sidecar.Type),
					MountCommand.Cell(console, d.Sidecar.Status, d.Class switch
					{
						DocStatusClass.Open => "yellow",
						DocStatusClass.Current => "green",
						_ => "gray"
					}),
					MountCommand.Cell(console, d.Sidecar.Title),
					MountCommand.Cell(console, d.File.Path, "gray"));
			console.Write(table);
		}
		return console.Report(diagnostics);
	}

	static int Show(FxConsole console, string selector)
	{
		var diagnostics = new List<FxDiagnostic>();
		void Json(JsonDoc? document, IReadOnlyList<JsonDocRelation> relations, IReadOnlyList<JsonDocRelation> inverse)
			=> console.WriteJson(new DocShowDocument(ShowSchema, FxConsole.Ok(diagnostics), document, relations, inverse, JsonDiagnostic.From(diagnostics)),
				c => c.DocShowDocument);
		if (DocsModule.Find(console.Settings.Directory, diagnostics) is not { } catalog)
		{
			if (console.Settings.IsMachine)
				Json(null, [], []);
			return console.Report(diagnostics);
		}
		if (Select(console, catalog, [selector]) is not { } files)
			return FxConsole.UsageError;
		if (files.Count > 1)
		{
			console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get("doc.many-documents", selector, files.Count))}[/]");
			return FxConsole.UsageError;
		}
		var documents = DocChecker.Read(catalog);
		var byId = documents.GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		if (documents.FirstOrDefault(d => d.File == files[0]) is not { } shown)
		{
			// it cannot be read: what is wrong with it
			diagnostics.AddRange(DocChecker.Check(catalog, Display(catalog), files).Diagnostics.Where(d => d.Severity == FxSeverity.Error));
			if (console.Settings.IsMachine)
				Json(null, [], []);
			return console.Report(diagnostics);
		}
		var relations = shown.Sidecar.Relations
			.Select(r => byId.GetValueOrDefault(r.TargetId) is { } target ? new JsonDocRelation(r.Kind, r.TargetId, target.File.Path, target.Sidecar.Title) : new(r.Kind, r.TargetId, null, null))
			.ToList();
		var inverse = documents
			.SelectMany(d => d.Sidecar.Relations.Where(r => string.Equals(r.TargetId, shown.Sidecar.Id, StringComparison.OrdinalIgnoreCase))
				.Select(r => new JsonDocRelation(r.Kind, d.Sidecar.Id, d.File.Path, d.Sidecar.Title)))
			.OrderBy(r => r.Kind, StringComparer.Ordinal)
			.ThenBy(r => r.Path, StringComparer.Ordinal)
			.ToList();
		if (console.Settings.IsMachine)
		{
			Json(Doc(shown), relations, inverse);
			return 0;
		}
		var s = shown.Sidecar;
		console.Line($"[bold]{FxConsole.Escape(s.Title)}[/]");
		console.Line($"{FxConsole.Escape(s.Type)}{(s.Profile is null ? "" : $" · {FxConsole.Escape(s.Profile)}")} · {FxConsole.Escape(s.Status)} [gray]({ClassName(shown.Class)})[/]");
		console.Line($"[gray]{FxConsole.Escape(shown.File.Path)} · {FxConsole.Escape(s.Id)}[/]");
		console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.show.dates", s.CreatedAt, s.CreatedBy, s.UpdatedAt))}[/]");
		if (s.Tags.Count > 0)
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.show.tags", string.Join(", ", s.Tags.Order(StringComparer.Ordinal))))}[/]");
		void Relations(string title, IReadOnlyList<JsonDocRelation> items, string arrow)
		{
			if (items.Count == 0)
				return;
			console.Line();
			console.Line(FxConsole.Escape(Texts.Get(title)));
			foreach (var r in items)
				console.Line($"  {FxConsole.Escape(r.Kind)} {arrow} {(r.Path is null ? $"[red]{FxConsole.Escape(r.Id)}[/]" : $"{FxConsole.Escape(r.Title)} [gray]({FxConsole.Escape(r.Path)})[/]")}");
		}
		Relations("doc.show.relations", relations, "→");
		Relations("doc.show.inverse", inverse, "←");
		return 0;
	}

	static int Schema(FxConsole console, string? type)
	{
		// with a type, its schema: the data, always JSON whatever --output
		if (type is not null)
		{
			console.Raw(DocsSchemas.Texts[$"types/{type}.schema.json"].TrimEnd());
			return 0;
		}
		var types = DocTypes.All.Select(t => new JsonDocType(t.Name, t.Token, t.Temporality.ToString().ToLowerInvariant(),
			t.Statuses.Select(s => new JsonDocStatus(s, ClassName(t.ClassOf(s)))).ToList(), t.InitialStatus, t.Profiles, t.ProfileRequired,
			t.GenerationRequired ? "required" : t.GenerationAllowed ? "allowed" : "none")).ToList();
		if (console.Settings.IsMachine)
		{
			var relations = DocRelations.Kinds.Select(k => DocRelations.Rule(k) is var rule ? new JsonDocRelationKind(k, rule.Sources, rule.Targets) : null!).ToList();
			console.WriteJson(new DocSchemasDocument(SchemasSchema, true, DocsSchemas.Version, types, relations, []), c => c.DocSchemasDocument);
			return 0;
		}
		var table = MountCommand.Table(console, Texts.Get("doc.column.type"), Texts.Get("doc.column.temporality"), Texts.Get("doc.column.statuses"),
			Texts.Get("doc.column.profiles"));
		foreach (var t in types)
			table.AddRow(
				MountCommand.Cell(console, t.Name),
				MountCommand.Cell(console, Texts.Get($"doc.temporality.{t.Temporality}")),
				MountCommand.Cell(console, string.Join(", ", t.Statuses.Select(s => $"{s.Name} ({s.Class})"))),
				MountCommand.Cell(console, t.Profiles.Count == 0 ? "" : string.Join(", ", t.Profiles), t.ProfileRequired ? null : "gray"));
		console.Write(table);
		console.Line();
		console.Line($"[gray]{FxConsole.Escape(Texts.Get("doc.schema.more"))}[/]");
		return 0;
	}
}
