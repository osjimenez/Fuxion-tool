using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Explore;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;
using Fuxion.Tools.Core.Worktree;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Fuxion.Tools.Cli.Ui;

/// <summary>A line of the list of problems (decision 14.2): its node and its diagnostic.</summary>
public sealed record UiProblem(FxTreeNode Node, FxDiagnostic Diagnostic)
{
	public override string ToString() => $"{(Diagnostic.Severity == FxSeverity.Error ? "✗" : "!")} {Node.Name} · {Diagnostic.Code} · {Diagnostic.Message}";
}

/// <summary>A line of the worktrees of a workspace (decision 14.12): the worktree and its changes, once git says them.</summary>
public sealed record UiWorktree(WorktreeInfo Info, int? Changes)
{
	public override string ToString()
		=> $"{Info.Card.Branch} · {Info.Card.From} · {string.Join(", ", Info.Card.Worktrees.Select(w => w.Repository))} · "
		   + (Changes switch { null => "…", 0 => Texts.Get("ui.worktree.clean"), var n => Texts.Get("ui.worktree.changes", n) });
}

/// <summary>
/// <c>fx ui</c>, the layer to see (plan O, O4.1, decision 14): the tree of the workspace on the left (<see cref="FxTree"/>),
/// a panel per kind of node on the right, the marks of the doctor in the tree and its list of problems, and a bar with
/// the keys. The doctor and git run in the background; what they find reaches the screen on its thread
/// (<see cref="OnUpdate"/>). From a file it goes to where it comes from (14.3), and a worktree opens as its own tree
/// (14.12); Alt+← goes back. It changes nothing: acting and editing are the next layers. Each panel shows the command
/// that does the same (14.10).
/// </summary>
public sealed class UiApp
{
	readonly ConcurrentQueue<Action> _toUi = new();
	readonly State<int> _version = new(0);
	readonly State<bool> _all = new(false);
	readonly State<bool> _problemsVisible = new(false);
	readonly State<string> _doctor = new("");
	readonly State<int> _workspaceTab = new(0);
	readonly State<int> _repositoryTab = new(0);
	readonly Dictionary<string, GitStatus> _status = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, IReadOnlyList<string>> _commits = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, int> _worktreeChanges = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<FxTreeNode, TreeNode> _nodes = [];
	readonly Dictionary<FxTreeNode, IReadOnlyList<string>> _origins = [];
	readonly List<(TreeNode Tree, FxTreeNode Node)> _lazy = [];
	// where Alt+← goes: the tree and the node, the last first
	readonly Stack<(string Tree, string Node)> _back = new();
	// the trees opened, with their doctor, to come back to them without running it again
	readonly Dictionary<string, (FxTreeNode Model, bool Done)> _trees = new(StringComparer.OrdinalIgnoreCase);
	readonly ToastHost _host = new();
	readonly Group _problems;
	// where fx ui was launched: the commands it shows are written from there (--root)
	readonly string _start;
	readonly State<bool> _logVisible = new(false);
	readonly List<UiLogEntry> _log = [];
	// the work in the background (the doctor, git, the preview of a dialog): an action waits for it, cancelled, before it
	// starts (a git running in a repository would not let it be parked)
	readonly List<Task> _tasks = [];
	CancellationTokenSource _work = new();
	WorktreeCard? _card;
	UiRunning? _running;

	public TreeView Tree { get; } = new();

	public ListBox<UiProblem> Problems { get; } = new();

	public Visual Root { get; }

	public FxTreeNode Model { get; private set; }

	/// <summary>Whether the doctor has finished (its marks are on the tree).</summary>
	public bool DoctorDone { get; private set; }

	public FxTreeNode? Selected => Tree.SelectedNode?.Data as FxTreeNode;

	/// <summary>Of the tree shown, if it is a worktree: its card.</summary>
	public WorktreeCard? Worktree => _card;

	/// <summary>Whether an action is running.</summary>
	public bool Busy => _running is not null;

	/// <summary>What was run from fx ui, the last first.</summary>
	public IReadOnlyList<UiLogEntry> Log => _log;

	public UiApp(FxTreeNode model, string start)
	{
		Model = model;
		_start = start;
		_card = WorktreeCard.Load(model.Path);
		Fill(model.Find(start) ?? model);

		var panel = new ComputedVisual(() =>
		{
			_ = _version.Value;
			return Panel(Selected);
		});
		var header = new Header
		{
			Left = new Markup(() =>
			{
				_ = _version.Value;
				var worktree = _card is null ? "" : $" [accent]{Esc(Texts.Get("ui.worktree", _card.Branch))}[/]";
				return $"[bold]fx ui[/] · {Esc(Model.Identity?.FullName ?? Model.Name)}{worktree} [muted]{Esc(Model.Path)}[/]";
			}) { Wrap = false },
			Right = new Markup(() => _doctor.Value) { Wrap = false }
		};
		var content = new HSplitter(Tree, new Border(panel).Padding(new Thickness(1, 0, 1, 0)).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch))
			.Ratio(0.36);
		Problems.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Enter || Problems.SelectedIndex is not (>= 0 and var index) || index >= Problems.Items.Count)
				return;
			Select(Problems.Items[index].Node);
			e.Handled = true;
		});
		// Enter on what is not on disk does its action: mounting it, bringing it back (14.1)
		Tree.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Enter || Selected is not { Kind: FxTreeKind.Declared or FxTreeKind.Parked } node || Actions(node) is not { Count: > 0 } actions)
				return;
			Confirm(actions[0]);
			e.Handled = true;
		});
		_problems = new Group().TopLeftText(Texts.Get("ui.problems")).Content(Problems);
		// at the bottom: the action running, the problems (F2), the log (F8) and the keys
		var bottom = new ComputedVisual(() =>
		{
			_ = _version.Value;
			var parts = new List<Visual>();
			if (_running is { } running)
				parts.Add(running.View());
			if (_problemsVisible.Value)
				parts.Add(_problems);
			if (_logVisible.Value)
				parts.Add(LogView());
			parts.Add(new CommandBar());
			return new VStack(parts.ToArray()).Spacing(0);
		});
		_host.Content(new DockLayout()
			.HorizontalAlignment(Align.Stretch)
			.VerticalAlignment(Align.Stretch)
			.Top(header)
			.Content(content)
			.Bottom(bottom));
		AddCommands();
		Root = _host;
	}

	void AddCommands()
	{
		Command Make(string id, string key, KeyGesture gesture, Action execute, Func<bool>? can = null)
			=> new()
			{
				Id = id,
				LabelMarkup = Esc(Texts.Get(key)),
				DescriptionMarkup = Esc(Texts.Get($"{key}.description")),
				Gesture = gesture,
				Importance = CommandImportance.Primary,
				Presentation = CommandPresentation.CommandBar,
				Execute = _ => execute(),
				// a key that does nothing here is not on the bar
				IsVisible = can is null ? null : _ => can()
			};
		_host.AddCommand(Make("fx.problems", "ui.key.problems", new(TerminalKey.F2), ToggleProblems));
		_host.AddCommand(Make("fx.all", "ui.key.all", new(TerminalKey.F3), ToggleAll));
		_host.AddCommand(Make("fx.origin", "ui.key.origin", new(TerminalKey.F4), () => GoToOrigin(), () => Selected is { } node && Origins(node).Count > 0));
		_host.AddCommand(Make("fx.refresh", "ui.key.refresh", new(TerminalKey.F5), Refresh, () => !Busy));
		_host.AddCommand(Make("fx.main", "ui.key.main", new(TerminalKey.F6), () => MainTree(), () => _card is not null && !Busy));
		_host.AddCommand(Make("fx.sync", "ui.key.sync", new(TerminalKey.F7), Sync, () => !Busy && Selected is { } node && Actions(node).Any(a => a.Command.StartsWith("fx sync", StringComparison.Ordinal))));
		_host.AddCommand(Make("fx.log", "ui.key.log", new(TerminalKey.F8), ToggleLog));
		_host.AddCommand(Make("fx.actions", "ui.key.actions", new(TerminalKey.F9), ShowActions, () => !Busy && Selected is { } node && Actions(node).Count > 0));
		_host.AddCommand(Make("fx.cancel", "ui.key.cancel", new(TerminalKey.Escape), CancelRun, () => Busy));
		_host.AddCommand(Make("fx.back", "ui.key.back", new(TerminalKey.Left, TerminalModifiers.Alt), () => Back(), () => _back.Count > 0 && !Busy));
	}

	// the tree

	bool Visible(FxTreeNode node) => _all.Value || node.Origin != FxOrigin.Local;

	/// <summary>The tree of the screen from the model, with <paramref name="select"/> selected and its parents open.</summary>
	void Fill(FxTreeNode select)
	{
		Tree.Roots.Clear();
		_nodes.Clear();
		_lazy.Clear();
		var root = Build(Model);
		root.IsExpanded = true;
		Tree.Roots.Add(root);
		Select(select);
	}

	TreeNode Build(FxTreeNode node)
	{
		var tree = new TreeNode(new Markup(Label(node)) { Wrap = false })
		{
			Icon = new Rune(Icon(node)),
			Data = node
		};
		tree.AddRightVisual(new Markup(() =>
		{
			_ = _version.Value;
			return Badge(node);
		}) { Wrap = false });
		_nodes[node] = tree;
		foreach (var child in node.Children.Where(Visible))
			tree.Children.Add(Build(child));
		// the rest, what does not matter to fx, with "all" on (14.6): loaded when it is opened
		if (node.HasMore && _all.Value)
		{
			tree.Children.Add(new TreeNode(new Markup("[muted]…[/]") { Wrap = false }));
			_lazy.Add((tree, node));
		}
		return tree;
	}

	/// <summary>Selects <paramref name="node"/> in the tree, opening its parents.</summary>
	public bool Select(FxTreeNode node)
	{
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
			if (_nodes.TryGetValue(parent, out var open))
				open.IsExpanded = true;
		return _nodes.TryGetValue(node, out var tree) && Tree.TrySelectNode(tree);
	}

	public void ToggleAll()
	{
		var selected = Selected ?? Model;
		_all.Value = !_all.Value;
		Fill(Visible(selected) ? selected : selected.Parent ?? Model);
		_version.Value++;
	}

	public void ToggleProblems() => _problemsVisible.Value = !_problemsVisible.Value;

	/// <summary>Reads the tree again and runs the doctor and git again.</summary>
	public void Refresh()
	{
		var path = Selected?.Path ?? Model.Path;
		Stop();
		_trees.Remove(Model.Path);
		Model = FxTree.Build(Model.Path) ?? Model;
		_origins.Clear();
		_status.Clear();
		_commits.Clear();
		_worktreeChanges.Clear();
		Problems.Items.Clear();
		DoctorDone = false;
		Fill(Model.Find(path) ?? Model);
		Start();
	}

	// going around: to the origin of a file, into a worktree and back

	/// <summary>Where <paramref name="node"/> comes from (14.3), the nearest first.</summary>
	public IReadOnlyList<string> Origins(FxTreeNode node)
	{
		if (node.Kind != FxTreeKind.File || node.Origin is FxOrigin.Hand or FxOrigin.Local)
			return [];
		if (!_origins.TryGetValue(node, out var origins))
			_origins[node] = origins = FxTree.Origins(Model, node);
		return origins;
	}

	/// <summary>The node of <paramref name="path"/>, loading the folders on the way if they are not loaded yet.</summary>
	FxTreeNode? Reveal(string path, out bool loaded)
	{
		loaded = false;
		while (Model.Find(path) is { } node)
		{
			if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
				return node;
			if (!node.HasMore)
				return null;
			FxTree.LoadMore(node);
			loaded = true;
		}
		return null;
	}

	/// <summary>Goes where the file selected comes from (its nearest origin); Alt+← comes back.</summary>
	public bool GoToOrigin()
	{
		if (Selected is not { } node || Origins(node) is not { Count: > 0 } origins || Reveal(origins[0], out var loaded) is not { } origin)
			return false;
		_back.Push((Model.Path, node.Path));
		Go(origin, loaded);
		return true;
	}

	/// <summary>Back to where it was before going to an origin or into another tree.</summary>
	public bool Back()
	{
		if (!_back.TryPop(out var at))
			return false;
		if (!string.Equals(at.Tree, Model.Path, StringComparison.OrdinalIgnoreCase))
			Switch(at.Tree);
		Go(Reveal(at.Node, out var loaded) ?? Model, loaded);
		return true;
	}

	/// <summary>Opens the tree of <paramref name="directory"/> (a worktree, 14.12, or the main tree of one).</summary>
	public bool OpenTree(string directory)
	{
		if (Busy)
			return false;
		_back.Push((Model.Path, Selected?.Path ?? Model.Path));
		Switch(directory);
		return true;
	}

	/// <summary>From a worktree, its main tree: the main clone of its root.</summary>
	public bool MainTree() => _card is not null && GitClient.Discover(Model.Path)?.MainRoot() is { } main && OpenTree(main);

	void Go(FxTreeNode node, bool loaded)
	{
		if (loaded || !Select(node))
			Fill(node);
		_version.Value++;
	}

	/// <summary>Shows another tree: the one already opened, as it was, or a new one, with its doctor and git.</summary>
	void Switch(string directory)
	{
		Stop();
		_trees[Model.Path] = (Model, DoctorDone);
		var top = WorkspaceCopies.Top(directory) ?? directory;
		if (_trees.TryGetValue(top, out var opened))
			(Model, DoctorDone) = opened;
		else
		{
			Model = FxTree.Build(directory) ?? Model;
			DoctorDone = false;
		}
		_card = WorktreeCard.Load(Model.Path);
		_origins.Clear();
		Fill(Model);
		if (DoctorDone)
			ShowDoctor(Model);
		else
		{
			Problems.Items.Clear();
			Start();
		}
		_version.Value++;
	}

	// acting (O4.2): the actions of a node, confirmed, run here with their progress, and their log

	/// <summary>What can be done on <paramref name="node"/>, the main action first.</summary>
	public IReadOnlyList<UiAction> Actions(FxTreeNode node) => UiAction.For(node, _start, _card);

	/// <summary>F7: syncing the root the node selected is in.</summary>
	public void Sync()
	{
		if (Selected is { } node && Actions(node).FirstOrDefault(a => a.Command.StartsWith("fx sync", StringComparison.Ordinal)) is { } sync)
			Confirm(sync);
	}

	/// <summary>F9: the actions of the node selected, each with its command; Enter on one asks to confirm it.</summary>
	public void ShowActions()
	{
		if (Busy || Selected is not { } node)
			return;
		var actions = Actions(node);
		if (actions.Count == 0)
		{
			_host.Show(new TextBlock(Texts.Get("ui.actions.none")), ToastSeverity.Info);
			return;
		}
		var list = new ListBox<UiAction>();
		foreach (var action in actions)
			list.Items.Add(action);
		list.SelectedIndex = 0;
		Dialog? dialog = null;
		list.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Enter || list.SelectedIndex is not (>= 0 and var index) || index >= list.Items.Count)
				return;
			dialog!.Close();
			Confirm(list.Items[index]);
			e.Handled = true;
		});
		dialog = new Dialog().Title(Texts.Get("ui.actions", node.Name)).IsModal(true).Padding(1).Content(list);
		Closable(dialog);
		dialog.Show();
		list.App?.Focus(list);
	}

	/// <summary>
	/// Before doing it: what it is, the command that does the same and, if the command can tell (<c>--dry-run</c>), what
	/// it would do, worked out in the background. Enter does it; Esc does not.
	/// </summary>
	public void Confirm(UiAction action)
	{
		if (Busy)
		{
			_host.Show(new TextBlock(Texts.Get("ui.busy")), ToastSeverity.Warning);
			return;
		}
		var preview = new State<UiOutcome?>(null);
		var cancel = new CancellationTokenSource();
		var body = new ComputedVisual(() =>
		{
			var lines = new List<Visual> { Line(Esc(action.Description)), Equivalent(action.Command) };
			if (action.Preview is not null)
			{
				lines.Add(Line($"[bold]{Esc(Texts.Get("ui.confirm.preview"))}[/]"));
				if (preview.Value is not { } outcome)
					lines.Add(new HStack(new Spinner(), new TextBlock(Texts.Get("ui.confirm.computing"))).Spacing(1));
				else if (outcome.Lines.Count == 0 && outcome.Diagnostics.Count == 0)
					lines.Add(Line($"[muted]{Esc(Texts.Get("ui.confirm.nothing"))}[/]"));
				else
					lines.Add(new ScrollViewer(new VStack(OutcomeLines(outcome).Select(l => (Visual)Line(l)).ToArray()).Spacing(0)).MaxHeight(16));
			}
			lines.Add(Line($"[muted]{Esc(Texts.Get("ui.confirm.keys"))}[/]"));
			return new VStack(lines.ToArray()).Spacing(1);
		});
		var dialog = new Dialog().Title(action.Label).IsModal(true).Padding(1).Width(110).Content(body);
		dialog.KeyDown((_, e) =>
		{
			if (e.Key == TerminalKey.Enter)
			{
				cancel.Cancel();
				dialog.Close();
				Run(action);
				e.Handled = true;
			}
			else if (e.Key == TerminalKey.Escape)
			{
				cancel.Cancel();
				dialog.Close();
				e.Handled = true;
			}
		});
		dialog.Show();
		dialog.App?.Focus(dialog);
		if (action.Preview is { } work)
			_tasks.Add(Task.Run(() =>
			{
				UiOutcome outcome;
				try
				{
					outcome = work(cancel.Token);
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

	static void Closable(Dialog dialog)
		=> dialog.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Escape)
				return;
			dialog.Close();
			e.Handled = true;
		});

	/// <summary>The lines of an outcome: what changed, and each diagnostic with its fix.</summary>
	static IEnumerable<string> OutcomeLines(UiOutcome outcome)
	{
		foreach (var line in outcome.Lines)
			yield return line;
		foreach (var d in outcome.Diagnostics)
		{
			yield return $"{(d.Severity == FxSeverity.Error ? "[error]✗[/]" : "[warning]![/]")} {Esc(d.Message.ToString())} [muted]({Esc(d.Code)})[/]";
			if (d.Fix is { } fix)
				yield return $"    [muted]→ {Esc(fix.Command ?? fix.Hint?.ToString())}[/]";
		}
	}

	/// <summary>Runs <paramref name="action"/> in the background, with its progress at the bottom; then the log, and the tree read again.</summary>
	void Run(UiAction action)
	{
		// what the doctor and git look at is about to change
		Stop();
		var running = _running = new UiRunning(action);
		_version.Value++;
		var pending = _tasks.ToArray();
		_tasks.Clear();
		Task.Run(() =>
		{
			try
			{
				Task.WaitAll(pending);
			}
			catch (AggregateException)
			{
				// cancelled, or failed: what matters is that it is over
			}
			UiOutcome? outcome = null;
			var cancelled = false;
			try
			{
				outcome = action.Run(running, running.Cancel.Token);
			}
			catch (OperationCanceledException)
			{
				cancelled = true;
			}
			catch (Exception ex)
			{
				// what the core did not expect (a manifest that does not read): its message, as it is
				outcome = new([], [FxDiagnostic.Error("ui.failed", FxText.Plain(ex.Message))]);
			}
			_toUi.Enqueue(() => Finish(running, outcome, cancelled || running.Cancel.IsCancellationRequested));
		});
	}

	void Finish(UiRunning running, UiOutcome? outcome, bool cancelled)
	{
		running.Drain();
		_log.Insert(0, new(DateTime.Now, running.Action, outcome, cancelled, running.Steps.ToList()));
		_running = null;
		var (key, severity) = cancelled ? ("ui.result.cancelled", ToastSeverity.Warning)
			: outcome?.Ok == true ? ("ui.result.done", ToastSeverity.Success)
			: ("ui.result.errors", ToastSeverity.Error);
		_host.Show(new TextBlock(Texts.Get(key, running.Action.Command)), severity);
		// the disk changed: every tree opened is read again
		_trees.Clear();
		Refresh();
	}

	public void CancelRun()
	{
		_running?.Cancel.Cancel();
		_version.Value++;
	}

	public void ToggleLog() => _logVisible.Value = !_logVisible.Value;

	/// <summary>The log (F8): each command run from fx ui, the last first, with what it did (the decision of the commands log, 14.10).</summary>
	Visual LogView()
	{
		var lines = new List<string>();
		foreach (var entry in _log)
		{
			var mark = entry.Cancelled ? "[warning]·[/]" : entry.Outcome?.Ok == true ? "[success]✓[/]" : "[error]✗[/]";
			lines.Add($"{mark} [muted]{entry.When:HH:mm:ss}[/] [accent]{Esc(entry.Action.Command)}[/]");
			lines.AddRange(entry.Steps.Concat(entry.Outcome is null ? [] : OutcomeLines(entry.Outcome)).Select(l => $"    {l}"));
		}
		if (lines.Count == 0)
			lines.Add($"[muted]{Esc(Texts.Get("ui.log.empty"))}[/]");
		return new Group().TopLeftText(Texts.Get("ui.log")).HorizontalAlignment(Align.Stretch)
			.Content(new ScrollViewer(new VStack(lines.Select(l => (Visual)new Markup(l) { Wrap = false }).ToArray()).Spacing(0)).MaxHeight(12));
	}

	// the work in the background: the doctor (offline) and git

	public void Start()
	{
		_work = new();
		var token = _work.Token;
		var model = Model;
		_doctor.Value = $"[muted]{Esc(Texts.Get("ui.doctor.running"))}[/]";
		_tasks.RemoveAll(t => t.IsCompleted);
		_tasks.Add(Task.Run(() =>
		{
			IReadOnlyList<FxModuleResult> results;
			try
			{
				results = SyncEngine.Doctor(model.Path, new(Offline: true), cancellationToken: token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				_toUi.Enqueue(() => _doctor.Value = $"[error]{Esc(ex.Message)}[/]");
				return;
			}
			_toUi.Enqueue(() =>
			{
				if (model != Model)
					return;
				FxTree.Attach(model, results);
				ShowDoctor(model);
				DoctorDone = true;
			});
		}, token));
		_tasks.Add(Task.Run(() =>
		{
			var all = model.Descendants().Prepend(model).ToList();
			var repositories = all.Where(n => n.Kind is FxTreeKind.Repository or FxTreeKind.Workspace).ToList();
			Parallel.ForEach(repositories, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, repository =>
			{
				if (GitClient.Discover(repository.Path) is not { } git)
					return;
				var status = git.Status();
				var commits = git.RecentCommits(5);
				_toUi.Enqueue(() =>
				{
					_status[repository.Path] = status;
					_commits[repository.Path] = commits;
				});
			});
			// the changes of each worktree (14.12)
			foreach (var workspace in all.Where(n => n.Worktrees.Count > 0))
				foreach (var worktree in WorktreeModule.Of(workspace.Path, withStatus: true))
				{
					token.ThrowIfCancellationRequested();
					var changes = worktree.Changes?.Values.Sum() ?? 0;
					_toUi.Enqueue(() => _worktreeChanges[worktree.Root] = changes);
				}
		}, token));
	}

	/// <summary>The diagnostics of the doctor, already on the tree: its list and its summary.</summary>
	void ShowDoctor(FxTreeNode model)
	{
		Problems.Items.Clear();
		foreach (var node in model.Descendants().Prepend(model))
			foreach (var diagnostic in node.Diagnostics.Where(d => d.Severity != FxSeverity.Info).OrderBy(d => d.Severity))
				Problems.Items.Add(new(node, diagnostic));
		var problems = model.Problems;
		_doctor.Value = problems == 0
			? $"[success]✓ {Esc(Texts.Get("ui.doctor.clean"))}[/]"
			: $"[{(model.Worst == FxSeverity.Error ? "error" : "warning")}]! {Esc(Texts.Get("ui.doctor.problems", problems))}[/]";
	}

	public void Stop() => _work.Cancel();

	/// <summary>On the thread of the screen: what the background found, and the folders opened that load the rest.</summary>
	public TerminalLoopResult OnUpdate()
	{
		// the progress of the action running
		var changed = _running?.Drain() ?? false;
		while (_toUi.TryDequeue(out var action))
		{
			action();
			changed = true;
		}
		foreach (var (tree, node) in _lazy.Where(l => l.Tree.IsExpanded).ToList())
		{
			_lazy.Remove((tree, node));
			FxTree.LoadMore(node);
			tree.Children.Clear();
			foreach (var child in node.Children.Where(Visible))
				tree.Children.Add(Build(child));
			changed = true;
		}
		if (changed)
			_version.Value++;
		return TerminalLoopResult.Continue;
	}

	// what each node looks like

	static string Esc(string? text) => FxConsole.Escape(text);

	static char Icon(FxTreeNode node) => node.Kind switch
	{
		FxTreeKind.Workspace => '◎',
		FxTreeKind.Repository => '▣',
		FxTreeKind.Declared => '░',
		FxTreeKind.Parked => '▤',
		FxTreeKind.Unknown => '?',
		FxTreeKind.Module => '◆',
		FxTreeKind.ModuleOff => '◇',
		FxTreeKind.Folder => node.Origin == FxOrigin.Local ? '~' : '▪',
		_ => node.Origin switch
		{
			FxOrigin.Generated => '⚙',
			FxOrigin.Inherited => '⇣',
			FxOrigin.Share => '⇢',
			FxOrigin.Local => '~',
			_ => '•'
		}
	};

	static string Label(FxTreeNode node)
	{
		var name = Esc(node.Name);
		return node.Kind switch
		{
			FxTreeKind.Workspace or FxTreeKind.Repository => $"[bold]{name}[/]",
			FxTreeKind.Unknown => $"[warning]{name}[/]",
			FxTreeKind.Declared or FxTreeKind.Parked or FxTreeKind.ModuleOff => $"[muted]{name}[/]",
			FxTreeKind.Module when !node.Enabled => $"[muted]{name}[/]",
			_ when node.Origin is FxOrigin.Generated or FxOrigin.Inherited or FxOrigin.Local => $"[muted]{name}[/]",
			_ => name
		};
	}

	/// <summary>The mark on the right: git and the doctor for a repository; the state of one not on disk; the doctor elsewhere.</summary>
	string Badge(FxTreeNode node)
	{
		var parts = new List<string>();
		// a copy that is not used is not cloned, nor is it going to be: what it is says enough
		if (node.Kind == FxTreeKind.Declared && node.SupersededBy is null)
			parts.Add($"[muted]{Esc(Texts.Get(Absent(node)))}[/]");
		else if (node.Kind == FxTreeKind.Parked)
			parts.Add($"[muted]{Esc(Texts.Get("ui.state.parked"))}[/]");
		if (node.SupersededBy is not null)
			parts.Add($"[muted]{Esc(Texts.Get("ui.state.superseded"))}[/]");
		if (node.Guest)
			parts.Add($"[muted]{Esc(Texts.Get("ui.state.guest"))}[/]");
		if (_status.TryGetValue(node.Path, out var s))
		{
			var git = new List<string>();
			if (s.Ahead > 0) git.Add($"↑{s.Ahead}");
			if (s.Behind > 0) git.Add($"↓{s.Behind}");
			if (s.Changes > 0) git.Add($"*{s.Changes}");
			if (git.Count > 0)
				parts.Add($"[warning]{string.Join(' ', git)}[/]");
		}
		var problems = node.Problems;
		if (problems > 0)
			parts.Add(node.Worst == FxSeverity.Error ? $"[error]✗{problems}[/]" : $"[warning]!{problems}[/]");
		else if (DoctorDone && node.IsRepository && node.Kind is FxTreeKind.Repository or FxTreeKind.Workspace)
			parts.Add("[success]✓[/]");
		return string.Join(' ', parts);
	}

	// the panels (decision 14)

	Visual Panel(FxTreeNode? node) => node?.Kind switch
	{
		null => new TextBlock(Texts.Get("ui.select")),
		FxTreeKind.Workspace => WorkspacePanel(node),
		FxTreeKind.Repository => RepositoryPanel(node),
		FxTreeKind.Declared or FxTreeKind.Parked => AbsentPanel(node),
		FxTreeKind.Unknown => UnknownPanel(node),
		FxTreeKind.Module or FxTreeKind.ModuleOff => ModulePanel(node),
		FxTreeKind.File => FilePanel(node),
		_ => FolderPanel(node)
	};

	static Markup Line(string markup) => new(markup) { Wrap = true };

	/// <summary>What does the same from the command line (14.10).</summary>
	static Markup Equivalent(string command) => Line($"[muted]{Esc(Texts.Get("ui.equivalent"))}[/] [accent]{Esc(command)}[/]");

	static string Relative(FxTreeNode from, FxTreeNode node) => FxLayout.Relative(from.Path, node.Path);

	static FxTreeNode WorkspaceOf(FxTreeNode node)
	{
		for (var at = node.Parent; at is not null; at = at.Parent)
			if (at.Kind == FxTreeKind.Workspace)
				return at;
		return node;
	}

	/// <summary>The repositories a workspace declares itself (not those of the workspaces below it).</summary>
	static IEnumerable<FxTreeNode> Declared(FxTreeNode workspace)
		=> workspace.Descendants().Where(n => n.Declaration is not null && WorkspaceOf(n) == workspace);

	Visual Title(FxTreeNode node)
	{
		var lines = new List<Visual> { Line($"[bold]{Icon(node)} {Esc(node.Name)}[/]  {Esc(node.Identity?.FullName ?? "")}  [muted]{Esc(node.Path)}[/]") };
		if (node.Declaration is { } declaration)
		{
			var tags = declaration.Tags.Count == 0 ? "—" : string.Join(", ", declaration.Tags);
			lines.Add(Line($"{Esc(Texts.Get("ui.tags"))}: [accent]{Esc(tags)}[/]   {Esc(Texts.Get("ui.mount"))}: {Esc(Texts.Get(declaration.Mount == MountPolicy.Mandatory ? "ui.mount.mandatory" : "ui.mount.manual"))}"));
		}
		if (node.Guest)
			lines.Add(Line($"[muted]{Esc(Texts.Get("ui.guest"))}[/]"));
		if (node.SupersededBy is { } higher)
			lines.Add(Line($"[warning]{Esc(Texts.Get("ui.superseded", higher.FullName))}[/]"));
		return new VStack(lines.ToArray()).Spacing(0);
	}

	Visual WorkspacePanel(FxTreeNode node)
	{
		var repos = new Table().Headers(new TextBlock(Texts.Get("ui.column.repository")), new TextBlock(Texts.Get("ui.column.state")),
			new TextBlock(Texts.Get("ui.column.branch")), new TextBlock(Texts.Get("ui.column.doctor")));
		foreach (var repo in Declared(node).OrderByDescending(r => r.Problems).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
			repos.AddRow(new Markup(Label(repo)) { Wrap = false }, new TextBlock(State(repo)),
				new TextBlock(_status.TryGetValue(repo.Path, out var s) ? s.Branch ?? "—" : ""), new Markup(Badge(repo)) { Wrap = false });

		// the tab chosen stays when the panel is drawn again (the background brings news) or another workspace is selected
		return new VStack(Title(node), new TabControl(
			new TabPage(Texts.Get("ui.tab.repositories"), repos),
			new TabPage(Texts.Get("ui.tab.problems"), ProblemsOf(node)),
			new TabPage(Texts.Get("ui.tab.versions"), Versions(node)),
			new TabPage(Texts.Get("ui.tab.worktrees"), Worktrees(node)),
			new TabPage(Texts.Get("ui.tab.applies"), Applies(node))).SelectedIndex(_workspaceTab)).Spacing(1);
	}

	string State(FxTreeNode repo) => Texts.Get(repo.Kind switch
	{
		FxTreeKind.Declared => Absent(repo),
		FxTreeKind.Parked => "ui.state.parked",
		_ when repo.SupersededBy is not null => "ui.state.superseded",
		_ => "ui.state.mounted"
	});

	/// <summary>What a workspace versions (its paths) and what it shares, and whom (14.5).</summary>
	static Visual Versions(FxTreeNode node)
	{
		var file = FxLayout.Full(node.Path, WorkspaceManifest.RelativePath);
		var manifest = WorkspaceManifest.Read(node.Path, File.Exists(file) ? File.ReadAllText(file) : "");
		var text = new StringBuilder();
		text.AppendLine($"[bold]{Esc(Texts.Get("ui.paths"))}[/]");
		foreach (var path in manifest.Paths ?? [])
			text.AppendLine($"  {(path.Exclude ? "[muted]− " : "+ ")}{Esc(path.Path)}{(path.Exclude ? "[/]" : "")}{(path.Worktree ? "" : $" [muted]({Esc(Texts.Get("ui.no-worktree"))})[/]")}");
		text.AppendLine();
		text.AppendLine($"[bold]{Esc(Texts.Get("ui.shares"))}[/]");
		foreach (var share in manifest.Shares ?? [])
		{
			var reach = node.Identity is { } identity ? FxTree.Reach(node, share.AppliesTo.Absolute(identity.FullName)).Count(r => r.Applies) : 0;
			text.AppendLine($"  {Esc(share.Path)} → {Esc(share.Destination)}  [muted]{Esc(Texts.Get("ui.reaches", reach))}[/]");
		}
		if ((manifest.Shares ?? []).Count == 0)
			text.AppendLine($"  [muted]—[/]");
		return new ScrollViewer(new Markup(text.ToString().TrimEnd()) { Wrap = false });
	}

	/// <summary>The worktrees of a workspace, with their changes; Enter opens one (14.12).</summary>
	Visual Worktrees(FxTreeNode node)
	{
		if (node.Worktrees.Count == 0)
			return new VStack(Line($"[muted]{Esc(Texts.Get("ui.worktrees.none"))}[/]"), Equivalent("fx worktree add <branch>")).Spacing(1);
		var list = new ListBox<UiWorktree>();
		foreach (var worktree in node.Worktrees)
			list.Items.Add(new(worktree, _worktreeChanges.TryGetValue(worktree.Root, out var changes) ? changes : null));
		list.KeyDown((_, e) =>
		{
			if (e.Key != TerminalKey.Enter || list.SelectedIndex is not (>= 0 and var index) || index >= list.Items.Count)
				return;
			OpenTree(list.Items[index].Info.Root);
			e.Handled = true;
		});
		return new VStack(list, Line($"[muted]{Esc(Texts.Get("ui.worktree.open"))}[/]"), Equivalent("fx worktree list")).Spacing(1);
	}

	Visual RepositoryPanel(FxTreeNode node)
	{
		var state = new List<Visual>();
		if (_status.TryGetValue(node.Path, out var s))
		{
			state.Add(Line($"{Esc(Texts.Get("ui.column.branch"))}: [bold]{Esc(s.Branch ?? Texts.Get("ui.detached"))}[/]   upstream: {Esc(s.Upstream ?? "—")}"));
			state.Add(Line(Esc(Texts.Get("ui.git", s.Ahead, s.Behind, s.Changes))));
		}
		else
			state.Add(Line("[muted]…[/]"));
		if (node.Declaration?.Url is { } url)
			state.Add(Line($"url: {Esc(url)}"));
		if (_commits.TryGetValue(node.Path, out var commits) && commits.Count > 0)
		{
			state.Add(Line($"[bold]{Esc(Texts.Get("ui.commits"))}[/]"));
			state.AddRange(commits.Select(c => (Visual)Line($"  [muted]{Esc(c)}[/]")));
		}
		return new VStack(Title(node), new TabControl(
			new TabPage(Texts.Get("ui.tab.state"), new VStack(state.ToArray()).Spacing(0)),
			new TabPage(Texts.Get("ui.tab.applies"), Applies(node)),
			new TabPage(Texts.Get("ui.tab.problems"), ProblemsOf(node))).SelectedIndex(_repositoryTab)).Spacing(1);
	}

	static string Why(FxReason reason) => reason.Kind switch
	{
		FxReasonKind.Everyone => Texts.Get("ui.why.everyone"),
		FxReasonKind.Tag => Texts.Get("ui.why.tag", string.Join(", ", reason.Values)),
		FxReasonKind.Name => Texts.Get("ui.why.name"),
		FxReasonKind.Conduit => Texts.Get("ui.why.conduit"),
		_ => Texts.Get("ui.why.not", string.Join(", ", reason.Values))
	};

	/// <summary>What reaches it, and why (14.4).</summary>
	static Visual Applies(FxTreeNode node)
	{
		var applied = FxTree.Applies(node);
		if (applied.Count == 0)
			return Line($"[muted]{Esc(Texts.Get("ui.applies.none"))}[/]");
		var table = new Table().Headers(new TextBlock(Texts.Get("ui.column.what")), new TextBlock(Texts.Get("ui.column.name")), new TextBlock(Texts.Get("ui.column.reaches")),
			new TextBlock(Texts.Get("ui.column.why")), new TextBlock(Texts.Get("ui.column.from")));
		foreach (var a in applied)
			table.AddRow(new TextBlock(a.What), new TextBlock(a.Name), new Markup(a.Applies ? "[success]✓[/]" : "[muted]—[/]") { Wrap = false },
				new TextBlock(Why(a.Reason)), new TextBlock(string.Join(", ", a.From)));
		return new ScrollViewer(table);
	}

	static Visual ProblemsOf(FxTreeNode node)
	{
		var problems = node.Descendants().Prepend(node).SelectMany(n => n.Diagnostics.Select(d => (Node: n, Diagnostic: d))).ToList();
		if (problems.Count == 0)
			return Line($"[success]✓ {Esc(Texts.Get("ui.doctor.clean"))}[/]");
		var text = new StringBuilder();
		foreach (var (at, d) in problems.OrderBy(p => p.Diagnostic.Severity))
		{
			var mark = d.Severity switch { FxSeverity.Error => "[error]✗[/]", FxSeverity.Warning => "[warning]![/]", _ => "[muted]·[/]" };
			text.AppendLine($"{mark} [bold]{Esc(at.Name)}[/] · {Esc(d.Code)}: {Esc(d.Message.ToString())}");
			if (d.Fix is { } fix)
				text.AppendLine($"    [muted]→ {Esc(fix.Command ?? fix.Hint?.ToString())}[/]");
		}
		return new ScrollViewer(new Markup(text.ToString().TrimEnd()) { Wrap = true });
	}

	Visual AbsentPanel(FxTreeNode node)
	{
		var lines = new List<Visual> { Title(node) };
		if (node.Kind == FxTreeKind.Parked)
			lines.Add(Line(Esc(Texts.Get("ui.parked", $"{FxLayout.Parking}/{Relative(WorkspaceOf(node), node)}"))));
		// in a worktree, fx sync neither clones nor parks: what is not in it is mounted with fx worktree mount
		else if (node.SupersededBy is null && _card is not null)
			lines.Add(Line(Esc(Texts.Get("ui.not-in-worktree"))));
		else if (node.SupersededBy is null)
			lines.Add(Line(Esc(Texts.Get(node.Mounted ? "ui.not-cloned" : "ui.not-mounted"))));
		// what Enter does (14.1), and the command that does the same
		if (Actions(node) is { Count: > 0 } actions)
			lines.Add(new VStack(Line($"[muted]Enter:[/] {Esc(actions[0].Label)}"), Equivalent(actions[0].Command)).Spacing(0));
		return new VStack(lines.ToArray()).Spacing(1);
	}

	/// <summary>The state of a repository declared and not on disk: in a worktree, not in it; else not cloned or not mounted.</summary>
	string Absent(FxTreeNode node) => _card is not null ? "ui.state.not-in-worktree" : node.Mounted ? "ui.state.not-cloned" : "ui.state.not-mounted";

	static Visual UnknownPanel(FxTreeNode node)
	{
		var workspace = WorkspaceOf(node);
		var relative = Relative(workspace, node);
		var repository = FxLayout.IsGitRoot(node.Path);
		var url = repository ? GitClient.Discover(node.Path)?.RemoteUrl("origin") ?? "<url>" : null;
		return new VStack(
			Line($"[warning]?[/] [bold]{Esc(node.Name)}[/]  [muted]{Esc(node.Path)}[/]"),
			Line(Esc(Texts.Get(repository ? "ui.unknown.repository" : "ui.unknown.folder"))),
			Equivalent(repository ? $"fx repo add {url} {relative}" : $"fx path add {relative}/")).Spacing(1);
	}

	Visual ModulePanel(FxTreeNode node)
	{
		if (node.Kind == FxTreeKind.ModuleOff)
			return new VStack(
				Line($"[muted]◇[/] [bold]{Esc(node.Name)}[/] · {Esc(Texts.Get("ui.module.unused"))}"),
				Line(Esc(Texts.Get("ui.module.enable"))),
				Equivalent($"fx module enable {node.Name}")).Spacing(1);
		var lines = new List<Visual>
		{
			Line($"[success]◆[/] [bold]{Esc(node.Name)}[/] · {Esc(Texts.Get(node.Enabled ? "ui.module.on" : "ui.module.off"))}  [muted]{Esc(node.Path)}[/]")
		};
		if (!node.Enabled)
			lines.Add(Equivalent($"fx module enable {node.Name}"));
		// the .NET of a workspace: whom it reaches (14.4, the other way)
		var root = node.Parent?.Parent;
		if (node.Name == "dotnet" && root is { Kind: FxTreeKind.Workspace })
		{
			var table = new Table().Headers(new TextBlock(Texts.Get("ui.column.repository")), new TextBlock(Texts.Get("ui.column.reaches")), new TextBlock(Texts.Get("ui.column.why")));
			foreach (var (repository, applies, reason) in FxTree.Reach(root, FxTree.DotnetAppliesTo(root)))
				table.AddRow(new TextBlock(repository.Identity?.FullName ?? repository.Name), new Markup(applies ? "[success]✓[/]" : "[muted]—[/]") { Wrap = false },
					new TextBlock(Why(reason)));
			lines.Add(new Group().TopLeftText(Texts.Get("ui.module.reaches")).Content(table));
		}
		return new VStack(lines.ToArray()).Spacing(1);
	}

	Visual FilePanel(FxTreeNode node)
	{
		var lines = new List<Visual> { Line($"[bold]{Icon(node)} {Esc(node.Name)}[/]  [muted]{Esc(node.Path)}[/]") };
		lines.Add(Line(node.Origin switch
		{
			FxOrigin.Generated => $"[muted]{Esc(Texts.Get("ui.file.generated"))}[/] [accent]fx sync[/]",
			FxOrigin.Inherited => $"[muted]{Esc(Texts.Get("ui.file.inherited", string.Join(", ", node.From)))}[/]",
			FxOrigin.Share => $"[muted]{Esc(Texts.Get("ui.file.share", string.Join(", ", node.From)))}[/]",
			FxOrigin.Local => $"[muted]{Esc(Texts.Get("ui.file.local"))}[/]",
			_ => $"[muted]{Esc(Texts.Get("ui.file.hand"))}[/]"
		}));
		// where it comes from (14.3), from the top of the tree
		if (Origins(node) is { Count: > 0 } origins)
			lines.Add(new VStack(origins.Select(o => (Visual)Line($"  [accent]{Esc(FxLayout.Relative(Model.Path, o))}[/]"))
				.Prepend(Line($"[muted]{Esc(Texts.Get("ui.origins"))}[/]")).ToArray()).Spacing(0));
		// the catalog of tags of a root (14.13)
		if (node.Name == "fx.yaml" && node.Parent is { Name: "_fx", Parent: { } root })
		{
			var table = new Table().Headers(new TextBlock(Texts.Get("ui.column.tag")), new TextBlock(Texts.Get("ui.column.description")), new TextBlock(Texts.Get("ui.column.from")),
				new TextBlock(Texts.Get("ui.column.used-by")));
			foreach (var tag in FxTree.Tags(root))
				table.AddRow(new TextBlock(tag.Tag.Name), new TextBlock(tag.Tag.Description ?? ""), new TextBlock(string.Join(", ", tag.From)),
					new TextBlock(string.Join(", ", tag.UsedBy.Select(u => u.Split('/').Last()))));
			lines.Add(new Group().TopLeftText(Texts.Get("ui.tags.catalog")).Content(table));
		}
		// a line each: a text block joins them
		lines.Add(new ScrollViewer(new VStack(Content(node.Path).Split('\n').Take(2000).Select(l => (Visual)new TextBlock(l) { Wrap = false }).ToArray()).Spacing(0)));
		return new VStack(lines.ToArray()).Spacing(1);
	}

	static string Content(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (info.Length > 200_000)
				return Texts.Get("ui.file.large");
			var bytes = File.ReadAllBytes(path);
			return bytes.AsSpan().IndexOf((byte)0) >= 0 ? Texts.Get("ui.file.binary") : Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\t', ' ');
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return ex.Message;
		}
	}

	Visual FolderPanel(FxTreeNode node)
		=> new VStack(
			Line($"[bold]{Icon(node)} {Esc(node.Name)}[/]  [muted]{Esc(node.Path)}[/]"),
			Line($"[muted]{Esc(Texts.Get(node.Origin == FxOrigin.Local ? "ui.folder.local" : node.Origin == FxOrigin.Inherited ? "ui.folder.inherited" : "ui.folder"))}[/]"),
			node.HasMore && !_all.Value ? Line($"[muted]{Esc(Texts.Get("ui.more"))}[/]") : new TextBlock("")).Spacing(1);
}
