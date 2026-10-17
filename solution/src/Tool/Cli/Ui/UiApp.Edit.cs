using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Cli.Describe;
using Fuxion.Tool.Cli.Sync;
using Fuxion.Tool.Core.Explore;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Workspace;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace Fuxion.Tool.Cli.Ui;

// editing (plan O, O4.3b; decision 14.9): the forms, the pending changes and applying them
public sealed partial class UiApp
{
	/// <summary>The changes not written yet (F10).</summary>
	public UiPending Pending { get; } = new();

	/// <summary>The form open, if any.</summary>
	public UiForm? Form { get; private set; }

	Dialog? _formDialog;

	/// <summary>A value as the shell takes it: in quotes if it has spaces.</summary>
	static string Q(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

	/// <summary>The workspace that declares <paramref name="node"/>, if it is in one.</summary>
	static FxTreeNode? ParentWorkspace(FxTreeNode node)
	{
		for (var at = node.Parent; at is not null; at = at.Parent)
			if (at.Kind == FxTreeKind.Workspace)
				return at;
		return null;
	}

	static UiAction Opening(string label, string command, Action open) => new(label, command, "", (_, _) => new([], [])) { Open = open };

	UiAction Staging(string label, UiEdit edit) => new(label, edit.Command, "", (_, _) => new([], [])) { Open = () => Stage(edit) };

	/// <summary>
	/// What edits <paramref name="node"/>, each with its command: a repository its workspace declares (its keys, its
	/// tags, its name; or out of the manifest), what nobody declares (added; 14.1), a module (on or off; 14.8) and what
	/// a workspace versions (its <c>paths</c>).
	/// </summary>
	IEnumerable<UiAction> EditActions(FxTreeNode node)
	{
		if (node.Declaration is { } declaration && ParentWorkspace(node) is { } owner && node.SupersededBy is null)
		{
			var root = UiAction.RootOption(_start, owner.Path);
			yield return Opening(Texts.Get("ui.edit.repository"), $"fx repo set|tag|rename {declaration.Name}{root}",
				() => ShowForm(RepositoryForm(AsPending(owner, declaration), owner)));
			var name = declaration.Name;
			yield return Staging(Texts.Get("ui.edit.remove"), new($"fx repo remove {name}{root}", () => [ManifestEditor.RemoveRepository(owner.Path, name)]));
		}
		if (node.Kind == FxTreeKind.Unknown && ParentWorkspace(node) is { } workspace)
		{
			var relative = FxLayout.Relative(workspace.Path, node.Path);
			var root = UiAction.RootOption(_start, workspace.Path);
			if (FxLayout.IsGitRoot(node.Path))
				yield return Opening(Texts.Get("ui.edit.add"), $"fx repo add <url> {relative}{root}", () => ShowForm(AddForm(node, workspace)));
			else
				yield return Staging(Texts.Get("ui.edit.version"), new($"fx path add {relative}/{root}", () => [ManifestEditor.AddPath(workspace.Path, $"{relative}/")]));
		}
		if (node.Kind is FxTreeKind.Module or FxTreeKind.ModuleOff && node.Parent?.Parent is { } owned)
		{
			var root = UiAction.RootOption(_start, owned.Path);
			var module = node.Name;
			if (node.Kind == FxTreeKind.ModuleOff || !node.Enabled)
				yield return Staging(Texts.Get("ui.edit.enable"), new($"fx module enable {module}{root}", () => [ManifestEditor.EnableModule(owned.Path, module, [])]));
			else
				yield return Staging(Texts.Get("ui.edit.disable"), new($"fx module disable {module}{root}", () => [ManifestEditor.DisableModule(owned.Path, module)]));
		}
		if (node.Kind == FxTreeKind.Workspace)
		{
			var root = UiAction.RootOption(_start, node.Path);
			yield return Opening(Texts.Get("ui.edit.path-add"), $"fx path add <path>{root}", () => ShowForm(PathForm(node)));
			yield return Opening(Texts.Get("ui.edit.path-remove"), $"fx path remove <path>{root}", () => ShowPathRemove(node));
		}
		// a yaml of fx, by hand (14.9, O4.3c)
		if (node.Kind == FxTreeKind.File && node.Origin == FxOrigin.Hand && ConfigOf(node.Path) is var (configRoot, configFile))
			yield return Opening(Texts.Get("ui.edit.yaml"), Texts.Get("ui.edit.by-hand", configFile), () => ShowYaml(configRoot, configFile));
	}

	/// <summary>A yaml of fx (in the <c>_fx/</c> of a root, not inherited nor local): its root and its path in it.</summary>
	static (string Root, string File)? ConfigOf(string path)
	{
		if (!path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.Split(Path.DirectorySeparatorChar).Any(FxLayout.IsLocal))
			return null;
		for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
			if (Path.GetFileName(dir) == FxLayout.Folder && Path.GetDirectoryName(dir) is { } root)
				return FxLayout.Relative(root, path) is var file && !file.StartsWith(FxLayout.Inherited, StringComparison.Ordinal) ? (root, file) : null;
		return null;
	}

	/// <summary>The raw editor open, if any (O4.3c).</summary>
	public CodeEditor? Editor { get; private set; }

	(string Root, string File, Dialog Dialog, State<string> Error)? _yaml;

	/// <summary>
	/// The raw YAML of a file of fx, editable (14.9): its text with the pending changes; Ctrl+S adds it to them, valid
	/// as fx reads it (the manifest, the yaml of a module, <c>fx.yaml</c>); Esc, nothing.
	/// </summary>
	public void ShowYaml(string root, string file)
	{
		var full = FxLayout.Full(root, file);
		string text;
		using (Pending.Overlay())
			text = FxFiles.Exists(full) ? FxFiles.ReadAllText(full) : "";
		var editor = YamlEditor(text.Replace("\r\n", "\n"));
		var error = new State<string>("");
		var dialog = new Dialog().Title(Texts.Get("ui.yaml.title", file)).IsModal(true).Padding(1).Width(120)
			.Content(new VStack(
				editor.MinHeight(24).MaxHeight(30),
				new Markup(() => error.Value.Length == 0 ? "" : $"[error]{Esc(error.Value)}[/]") { Wrap = true },
				Line($"[muted]{Esc(Texts.Get("ui.yaml.keys"))}[/]")).Spacing(1));
		dialog.KeyDown((_, e) =>
		{
			if (e.Key == TerminalKey.Escape)
			{
				CloseYaml();
				e.Handled = true;
			}
			else if (e is { Char: TerminalChar.CtrlS } or { Modifiers: TerminalModifiers.Ctrl, Char: 's' or 'S' })
			{
				AcceptYaml();
				e.Handled = true;
			}
		});
		Editor = editor;
		_yaml = (root, file, dialog, error);
		dialog.Show();
		editor.App?.Focus(editor);
	}

	void CloseYaml()
	{
		_yaml?.Dialog.Close();
		_yaml = null;
		Editor = null;
	}

	/// <summary>The text of the raw editor, to the pending changes, if it is valid; or why not, with its line.</summary>
	public bool AcceptYaml()
	{
		if (_yaml is not var (root, file, _, error) || Editor is not { } editor)
			return false;
		var text = editor.Text ?? "";
		var changes = Pending.Add(new(Texts.Get("ui.edit.by-hand", file), () => [ManifestEditor.Replace(root, file, text)]));
		if (changes.SelectMany(c => c.Diagnostics).FirstOrDefault(d => d.Severity == FxSeverity.Error) is { } bad)
		{
			error.Value = bad.Line is { } line ? $"{line}: {bad.Message}" : bad.Message.ToString();
			return false;
		}
		CloseYaml();
		_host.Show(new TextBlock(changes.Any(c => c.Changed) ? Texts.Get("ui.pending.added", Pending.Edits.Count) : Texts.Get("ui.form.nothing")), ToastSeverity.Info);
		_version.Value++;
		return true;
	}

	/// <summary>A YAML editor with a small highlighter of its own (keys, comments, list dashes): no TextMate, no native code (plan O, O1).</summary>
	static CodeEditor YamlEditor(string text) => new CodeEditor(text).Highlighter((in CodeEditorLineHighlightRequest request, List<StyledRun> runs) =>
	{
		var buffer = new char[request.LineLength];
		request.Snapshot.CopyTo(request.LineStart, buffer);
		var line = new string(buffer);
		var hash = line.IndexOf('#');
		if (hash >= 0 && (hash == 0 || char.IsWhiteSpace(line[hash - 1])))
			runs.Add(new StyledRun(hash, line.Length - hash, Style.None.WithForeground(Colors.Gray)));
		var trimmed = line.TrimStart();
		var start = line.Length - trimmed.Length;
		if (trimmed.StartsWith("- ", StringComparison.Ordinal))
		{
			runs.Add(new StyledRun(start, 1, Style.None.WithForeground(Colors.Goldenrod)));
			start += 2;
			trimmed = trimmed[2..];
		}
		var colon = trimmed.IndexOf(':');
		if (colon > 0 && (hash < 0 || start + colon < hash))
			runs.Add(new StyledRun(start, colon, Style.None.WithForeground(Colors.DeepSkyBlue)));
	});

	/// <summary>A declaration as the pending changes leave it (by its name, or by an old one if it is being renamed).</summary>
	WorkspaceRepository AsPending(FxTreeNode owner, WorkspaceRepository declaration)
	{
		try
		{
			using (Pending.Overlay())
				return WorkspaceManifest.Read(owner.Path, FxFiles.ReadAllText(ManifestEditor.ManifestPath(owner.Path))).Repositories
					.FirstOrDefault(r => r.Name == declaration.Name || (r.Aliases ?? []).Contains(declaration.Name)) ?? declaration;
		}
		catch (Exception ex) when (ex is IOException or WorkspaceManifestException)
		{
			return declaration;
		}
	}

	/// <summary>The declaration of a repository: what its workspace says of it (14.7, 14.9).</summary>
	UiForm RepositoryForm(WorkspaceRepository declaration, FxTreeNode owner)
	{
		var name = declaration.Name;
		var root = UiAction.RootOption(_start, owner.Path);
		return new(Texts.Get("ui.form.repository", name), [
			new("name", Texts.Get("ui.field.name"), name),
			new("url", Texts.Get("ui.field.url"), declaration.Url ?? ""),
			new("path", Texts.Get("ui.field.path"), declaration.Path),
			new("tags", Texts.Get("ui.field.tags"), string.Join(", ", declaration.Tags)),
			new("depends-on", Texts.Get("ui.field.depends-on"), string.Join(", ", declaration.DependsOn ?? [])),
			new("mandatory", Texts.Get("ui.field.mandatory"), check: declaration.Mount == MountPolicy.Mandatory),
			new("inherits", Texts.Get("ui.field.inherits"), check: declaration.Inherits)
		], form =>
		{
			var edits = new List<UiEdit>();
			var tags = form["tags"].Words;
			var added = tags.Except(declaration.Tags).ToList();
			var removed = declaration.Tags.Except(tags).ToList();
			if (added.Count > 0)
				edits.Add(new($"fx repo tag {name} {string.Join(' ', added)}{root}", () => [ManifestEditor.TagRepository(owner.Path, name, added)]));
			if (removed.Count > 0)
				edits.Add(new($"fx repo tag {name} {string.Join(' ', removed)} --remove{root}", () => [ManifestEditor.TagRepository(owner.Path, name, removed, remove: true)]));
			void Set(string key, IReadOnlyList<string> values)
				=> edits.Add(new(values.Count == 0 ? $"fx repo set {name} {key} --unset{root}" : $"fx repo set {name} {key} {string.Join(' ', values.Select(Q))}{root}",
					() => [ManifestEditor.SetRepository(owner.Path, name, key, values)]));
			if (form["url"].Value.Trim() is var url && url != (declaration.Url ?? ""))
				Set("url", url.Length == 0 ? [] : [url]);
			if (form["path"].Value.Trim() is var path && path != declaration.Path)
				Set("path", [path]);
			if (form["mandatory"].Check.Value != (declaration.Mount == MountPolicy.Mandatory))
				Set("mount", [form["mandatory"].Check.Value ? "mandatory" : "manual"]);
			if (form["inherits"].Check.Value != declaration.Inherits)
				Set("inherits", [form["inherits"].Check.Value ? "true" : "false"]);
			if (!form["depends-on"].Words.SequenceEqual(declaration.DependsOn ?? []))
				Set("depends-on", form["depends-on"].Words);
			// the name, the last: the edits above use the old one
			if (form["name"].Value.Trim() is { Length: > 0 } newName && newName != name)
				edits.Add(new($"fx repo rename {name} {newName}{root}", () => ManifestEditor.RenameRepository(owner.Path, name, newName)));
			return edits;
		}, ManifestEditor.ManifestPath(owner.Path));
	}

	/// <summary>A repository nobody declares, added to the workspace it is in (14.1).</summary>
	UiForm AddForm(FxTreeNode node, FxTreeNode workspace)
	{
		var url = GitClient.Discover(node.Path)?.RemoteUrl("origin") ?? "";
		var relative = FxLayout.Relative(workspace.Path, node.Path);
		var root = UiAction.RootOption(_start, workspace.Path);
		return new(Texts.Get("ui.form.add", relative), [
			new("url", Texts.Get("ui.field.url"), url),
			new("path", Texts.Get("ui.field.path"), relative),
			new("name", Texts.Get("ui.field.name"), url.Length > 0 ? ManifestEditor.NameFromUrl(url) : node.Name),
			new("tags", Texts.Get("ui.field.tags")),
			new("mandatory", Texts.Get("ui.field.mandatory"), check: false)
		], form =>
		{
			var (u, p, n) = (form["url"].Value.Trim(), form["path"].Value.Trim(), form["name"].Value.Trim());
			var mandatory = form["mandatory"].Check.Value;
			var tags = form["tags"].Words;
			var command = $"fx repo add {Q(u)} {Q(p)}{(n.Length > 0 && n != ManifestEditor.NameFromUrl(u) ? $" --name {n}" : "")}{(mandatory ? " --mandatory" : "")}"
			              + $"{(tags.Count > 0 ? $" --tag {string.Join(' ', tags)}" : "")}{root}";
			return [new(command, () => [ManifestEditor.AddRepository(workspace.Path, u, p, n.Length > 0 ? n : null, mandatory, null, tags)])];
		}, ManifestEditor.ManifestPath(workspace.Path));
	}

	/// <summary>Something more the workspace versions (its <c>paths</c>).</summary>
	UiForm PathForm(FxTreeNode workspace)
	{
		var root = UiAction.RootOption(_start, workspace.Path);
		return new(Texts.Get("ui.edit.path-add"), [
			new("path", Texts.Get("ui.field.path")),
			new("exclude", Texts.Get("ui.field.exclude"), check: false),
			new("worktree", Texts.Get("ui.field.worktree"), check: true)
		], form =>
		{
			var path = form["path"].Value.Trim();
			var (exclude, worktree) = (form["exclude"].Check.Value, form["worktree"].Check.Value);
			if (path.Length == 0)
				return [];
			return [new($"fx path add {Q(path)}{(exclude ? " --exclude" : "")}{(worktree ? "" : " --no-worktree")}{root}", () => [ManifestEditor.AddPath(workspace.Path, path, exclude, worktree)])];
		}, ManifestEditor.ManifestPath(workspace.Path));
	}

	/// <summary>Something the workspace stops versioning: its <c>paths</c> (with the pending ones), to choose one.</summary>
	void ShowPathRemove(FxTreeNode workspace)
	{
		IReadOnlyList<WorkspacePath> paths;
		using (Pending.Overlay())
			paths = WorkspaceManifest.Read(workspace.Path, FxFiles.ReadAllText(ManifestEditor.ManifestPath(workspace.Path))).Paths ?? [];
		var root = UiAction.RootOption(_start, workspace.Path);
		var list = new ListBox<string>();
		foreach (var path in paths)
			list.Items.Add(path.Path);
		Dialog? dialog = null;
		list.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Enter || list.SelectedIndex is not (>= 0 and var index) || index >= list.Items.Count)
				return;
			var path = list.Items[index];
			dialog!.Close();
			Stage(new($"fx path remove {Q(path)}{root}", () => [ManifestEditor.RemovePath(workspace.Path, path)]));
			e.Handled = true;
		});
		dialog = new Dialog().Title(Texts.Get("ui.edit.path-remove")).IsModal(true).Padding(1).Content(list);
		Closable(dialog);
		dialog.Show();
		list.App?.Focus(list);
	}

	/// <summary>A form in a dialog: Ctrl+S adds its edits to the pending changes; Esc, nothing.</summary>
	void ShowForm(UiForm form)
	{
		var width = form.Fields.Where(f => !f.IsCheck).Select(f => f.Label.Length).DefaultIfEmpty(0).Max() + 2;
		var inputs = form.Fields.Select(f => f.IsCheck ? (Visual)new CheckBox(new TextBlock(f.Label), f.Check) : new TextBox(f.Text).MinWidth(60)).ToList();
		var rows = form.Fields.Zip(inputs, (f, input) => f.IsCheck ? input : new HStack(new TextBlock(f.Label.PadRight(width)), input)).ToList();
		rows.Add(new Markup(() => form.Error.Value.Length == 0 ? "" : $"[error]{Esc(form.Error.Value)}[/]") { Wrap = true });
		// a button too (Tab, Enter): Ctrl+S may be the flow control of a terminal of Linux
		rows.Add(new Button(new TextBlock(Texts.Get("ui.form.add-button"))).Click(() => AcceptForm()));
		rows.Add(Line($"[muted]{Esc(Texts.Get("ui.form.keys"))}[/]"));
		var dialog = new Dialog().Title(form.Title).IsModal(true).Padding(1).Width(100).Content(new VStack(rows.ToArray()).Spacing(1));
		dialog.KeyDown((_, e) =>
		{
			if (e.Key == TerminalKey.Escape)
			{
				CloseForm();
				e.Handled = true;
			}
			else if (e is { Char: TerminalChar.CtrlS } or { Modifiers: TerminalModifiers.Ctrl, Char: 's' or 'S' })
			{
				AcceptForm();
				e.Handled = true;
			}
			// F4: its raw YAML, with what the form changes added to the pending ones first
			else if (e.Key == TerminalKey.F4 && form.File is { } path && ConfigOf(path) is var (root, file))
			{
				if (form.Edits().Count == 0)
					CloseForm();
				else if (!AcceptForm())
				{
					e.Handled = true;
					return;
				}
				ShowYaml(root, file);
				e.Handled = true;
			}
		});
		Form = form;
		_formDialog = dialog;
		dialog.Show();
		// the keys, to the form: it may be opened from the tree (Enter), which keeps the focus otherwise
		if (inputs.FirstOrDefault() is { } first)
			first.App?.Focus(first);
	}

	void CloseForm()
	{
		_formDialog?.Close();
		_formDialog = null;
		Form = null;
	}

	/// <summary>The edits of the form open, to the pending changes: all, or none if one is not valid (it says why).</summary>
	public bool AcceptForm()
	{
		if (Form is not { } form)
			return false;
		var edits = form.Edits();
		if (edits.Count == 0)
		{
			form.Error.Value = Texts.Get("ui.form.nothing");
			return false;
		}
		var added = 0;
		foreach (var edit in edits)
		{
			var changes = Pending.Add(edit);
			if (changes.SelectMany(c => c.Diagnostics).FirstOrDefault(d => d.Severity == FxSeverity.Error) is { } error)
			{
				for (var i = 0; i < added; i++)
					Pending.Undo();
				form.Error.Value = error.Message.ToString();
				return false;
			}
			if (changes.Any(c => c.Changed))
				added++;
		}
		CloseForm();
		_host.Show(new TextBlock(Texts.Get("ui.pending.added", Pending.Edits.Count)), ToastSeverity.Info);
		_version.Value++;
		return true;
	}

	/// <summary>An edit without a form, to the pending changes; or why it is not.</summary>
	void Stage(UiEdit edit)
	{
		var changes = Pending.Add(edit);
		if (changes.SelectMany(c => c.Diagnostics).FirstOrDefault(d => d.Severity == FxSeverity.Error) is { } error)
			_host.Show(new TextBlock(error.Message.ToString()), ToastSeverity.Error);
		else if (!changes.Any(c => c.Changed))
			_host.Show(new TextBlock(string.Join(" ", changes.SelectMany(c => c.Diagnostics).Select(d => d.Message.ToString())) is { Length: > 0 } why ? why : Texts.Get("ui.form.nothing")),
				ToastSeverity.Info);
		else
			_host.Show(new TextBlock(Texts.Get("ui.pending.added", Pending.Edits.Count)), ToastSeverity.Info);
		_version.Value++;
	}

	/// <summary>
	/// F10: the pending changes before they are written (14.9): each edit with its command, the diff of each file, and
	/// what <c>fx sync</c> would do with them, worked out in the background with them in place of the files. Enter
	/// writes them; Ctrl+Z takes back the last; Delete throws them all away; Esc leaves them pending.
	/// </summary>
	public void ShowPending()
	{
		if (!Pending.Any || Busy)
			return;
		var preview = new State<UiOutcome?>(null);
		var version = new State<int>(0);
		var cancel = new CancellationTokenSource();
		void Compute()
		{
			preview.Value = null;
			var top = Model.Path;
			_tasks.Add(Task.Run(() =>
			{
				UiOutcome outcome;
				try
				{
					using (Pending.Overlay())
						outcome = UiOutcome.From(SyncEngine.Sync(top, new(DryRun: true, FxVersion: SyncCommand.Informational, Skill: FxSkill.Text), cancellationToken: cancel.Token));
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					outcome = new([$"[error]{Esc(ex.Message)}[/]"], []);
				}
				_toUi.Enqueue(() => preview.Value = outcome);
			}, cancel.Token));
		}
		var body = new ComputedVisual(() =>
		{
			_ = version.Value;
			var lines = new List<string> { $"[bold]{Esc(Texts.Get("ui.pending.edits"))}[/]" };
			lines.AddRange(Pending.Edits.Select(e => $"  [accent]{Esc(e.Command)}[/]"));
			foreach (var (file, diff) in Pending.Diffs(Model.Path))
			{
				lines.Add("");
				lines.Add($"[bold]{Esc(file)}[/]");
				foreach (var hunk in diff)
				{
					lines.Add($"[muted]@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@[/]");
					lines.AddRange(hunk.Lines.Select(l => l.Kind switch
					{
						'-' => $"[error]-{Esc(l.Text)}[/]",
						'+' => $"[success]+{Esc(l.Text)}[/]",
						_ => $"[muted] {Esc(l.Text)}[/]"
					}));
				}
			}
			lines.Add("");
			lines.Add($"[bold]{Esc(Texts.Get("ui.pending.sync"))}[/]");
			var visuals = lines.Select(l => (Visual)new Markup(l) { Wrap = false }).ToList();
			if (preview.Value is not { } outcome)
				visuals.Add(new HStack(new Spinner(), new TextBlock(Texts.Get("ui.confirm.computing"))).Spacing(1));
			else if (outcome.Lines.Count == 0 && outcome.Diagnostics.Count == 0)
				visuals.Add(Line($"[muted]{Esc(Texts.Get("ui.confirm.nothing"))}[/]"));
			else
				visuals.AddRange(OutcomeLines(outcome).Select(l => (Visual)new Markup(l) { Wrap = false }));
			return new VStack(
				new ScrollViewer(new VStack(visuals.ToArray()).Spacing(0)).MaxHeight(30),
				Line($"[muted]{Esc(Texts.Get("ui.pending.keys"))}[/]")).Spacing(1);
		});
		var dialog = new Dialog().Title(Texts.Get("ui.pending.title", Pending.Edits.Count)).IsModal(true).Padding(1).Width(120).Content(body);
		dialog.KeyDown((_, e) =>
		{
			switch (e)
			{
				case { Key: TerminalKey.Enter }:
					cancel.Cancel();
					dialog.Close();
					ApplyPending();
					break;
				case { Key: TerminalKey.Delete }:
					cancel.Cancel();
					Pending.Discard();
					dialog.Close();
					_host.Show(new TextBlock(Texts.Get("ui.pending.discarded")), ToastSeverity.Info);
					_version.Value++;
					break;
				case { Char: TerminalChar.CtrlZ } or { Modifiers: TerminalModifiers.Ctrl, Char: 'z' or 'Z' }:
					Pending.Undo();
					_version.Value++;
					if (!Pending.Any)
					{
						cancel.Cancel();
						dialog.Close();
						break;
					}
					version.Value++;
					Compute();
					break;
				case { Key: TerminalKey.Escape }:
					cancel.Cancel();
					dialog.Close();
					break;
				default:
					return;
			}
			e.Handled = true;
		});
		dialog.Show();
		dialog.App?.Focus(dialog);
		Compute();
	}

	/// <summary>
	/// Writes the pending changes (or says why not) and keeps them in the log, each with its command; then the tree is
	/// read again and the sync of the tree is offered, with what it would do (the next step, as the commands say).
	/// </summary>
	public bool ApplyPending()
	{
		if (!Pending.Any || Busy)
			return false;
		var edits = Pending.Edits.ToList();
		var files = Pending.Diffs(_start).Select(d => d.File).ToList();
		var errors = Pending.Apply();
		if (errors.Count > 0)
		{
			_host.Show(new TextBlock(errors[0].Message.ToString()), ToastSeverity.Error);
			return false;
		}
		var written = new UiOutcome(files.Select(f => $"[warning]{Esc(Texts.Get("file.written"))}[/] {Esc(f)}").ToList(), []);
		foreach (var edit in edits)
			_log.Insert(0, new(DateTime.Now, new(Texts.Get("ui.pending.applied"), edit.Command, "", (_, _) => written), written, false, []));
		_host.Show(new TextBlock(Texts.Get("ui.pending.written", files.Count)), ToastSeverity.Success);
		_trees.Clear();
		Refresh();
		if (UiAction.For(Model, _start, _card).FirstOrDefault(a => a.Command.StartsWith("fx sync", StringComparison.Ordinal)) is { } sync)
			Confirm(sync);
		return true;
	}

	/// <summary>Ctrl+Q: not with an action running, and asking first with changes not written.</summary>
	void Quit(TerminalApp app)
	{
		if (Busy)
		{
			_host.Show(new TextBlock(Texts.Get("ui.quit.busy")), ToastSeverity.Warning);
			return;
		}
		if (!Pending.Any)
		{
			app.Stop();
			return;
		}
		var dialog = new Dialog().Title(Texts.Get("ui.quit.title")).IsModal(true).Padding(1)
			.Content(new VStack(Line(Esc(Texts.Get("ui.quit.pending", Pending.Edits.Count))), Line($"[muted]{Esc(Texts.Get("ui.quit.keys"))}[/]")).Spacing(1));
		dialog.KeyDown((_, e) =>
		{
			if (e.Key == TerminalKey.Enter)
			{
				dialog.Close();
				app.Stop();
				e.Handled = true;
			}
			else if (e.Key == TerminalKey.Escape)
			{
				dialog.Close();
				e.Handled = true;
			}
		});
		dialog.Show();
		dialog.App?.Focus(dialog);
	}
}
