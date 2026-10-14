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

/// <summary>
/// <c>fx ui</c>, the layer to see (plan O, O4.1, decision 14): the tree of the workspace on the left (<see cref="FxTree"/>),
/// a panel per kind of node on the right, the marks of the doctor in the tree and its list of problems, and a bar with
/// the keys. The doctor and git run in the background; what they find reaches the screen on its thread
/// (<see cref="OnUpdate"/>). It changes nothing: acting and editing are the next layers. Each panel shows the command
/// that does the same (14.10).
/// </summary>
public sealed class UiApp
{
	readonly string _start;
	readonly ConcurrentQueue<Action> _toUi = new();
	readonly State<int> _version = new(0);
	readonly State<bool> _all = new(false);
	readonly State<bool> _problemsVisible = new(false);
	readonly State<string> _doctor = new("");
	readonly Dictionary<string, GitStatus> _status = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, IReadOnlyList<string>> _commits = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<FxTreeNode, TreeNode> _nodes = [];
	readonly List<(TreeNode Tree, FxTreeNode Node)> _lazy = [];
	readonly ToastHost _host = new();
	readonly Group _problems;
	CancellationTokenSource _work = new();

	public TreeView Tree { get; } = new();

	public ListBox<UiProblem> Problems { get; } = new();

	public Visual Root { get; }

	public FxTreeNode Model { get; private set; }

	/// <summary>Whether the doctor has finished (its marks are on the tree).</summary>
	public bool DoctorDone { get; private set; }

	public FxTreeNode? Selected => Tree.SelectedNode?.Data as FxTreeNode;

	public UiApp(FxTreeNode model, string start)
	{
		Model = model;
		_start = start;
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
				return $"[bold]fx ui[/] · {Esc(Model.Identity?.FullName ?? Model.Name)} [muted]{Esc(Model.Path)}[/]";
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
		_problems = new Group().TopLeftText(Texts.Get("ui.problems")).Content(Problems);
		var bottom = new ComputedVisual(() => _problemsVisible.Value ? new VStack(_problems, new CommandBar()).Spacing(0) : new CommandBar());
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
		Command Make(string id, string label, string description, KeyGesture gesture, Action execute)
			=> new()
			{
				Id = id,
				LabelMarkup = Esc(label),
				DescriptionMarkup = Esc(description),
				Gesture = gesture,
				Importance = CommandImportance.Primary,
				Presentation = CommandPresentation.CommandBar,
				Execute = _ => execute()
			};
		_host.AddCommand(Make("fx.problems", Texts.Get("ui.key.problems"), Texts.Get("ui.key.problems.description"), new(TerminalKey.F2), ToggleProblems));
		_host.AddCommand(Make("fx.all", Texts.Get("ui.key.all"), Texts.Get("ui.key.all.description"), new(TerminalKey.F3), ToggleAll));
		_host.AddCommand(Make("fx.refresh", Texts.Get("ui.key.refresh"), Texts.Get("ui.key.refresh.description"), new(TerminalKey.F5), Refresh));
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
		Model = FxTree.Build(_start) ?? Model;
		_status.Clear();
		_commits.Clear();
		Problems.Items.Clear();
		DoctorDone = false;
		Fill(Model.Find(path) ?? Model);
		Start();
	}

	// the work in the background: the doctor (offline) and git

	public void Start()
	{
		_work = new();
		var token = _work.Token;
		var model = Model;
		_doctor.Value = $"[muted]{Esc(Texts.Get("ui.doctor.running"))}[/]";
		Task.Run(() =>
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
				Problems.Items.Clear();
				foreach (var node in model.Descendants().Prepend(model))
					foreach (var diagnostic in node.Diagnostics.Where(d => d.Severity != FxSeverity.Info).OrderBy(d => d.Severity))
						Problems.Items.Add(new(node, diagnostic));
				var problems = model.Problems;
				_doctor.Value = problems == 0
					? $"[success]✓ {Esc(Texts.Get("ui.doctor.clean"))}[/]"
					: $"[{(model.Worst == FxSeverity.Error ? "error" : "warning")}]! {Esc(Texts.Get("ui.doctor.problems", problems))}[/]";
				DoctorDone = true;
			});
		}, token);
		Task.Run(() =>
		{
			var repositories = model.Descendants().Prepend(model).Where(n => n.Kind is FxTreeKind.Repository or FxTreeKind.Workspace).ToList();
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
		}, token);
	}

	public void Stop() => _work.Cancel();

	/// <summary>On the thread of the screen: what the background found, and the folders opened that load the rest.</summary>
	public TerminalLoopResult OnUpdate()
	{
		var changed = false;
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
			parts.Add($"[muted]{Esc(Texts.Get(node.Mounted ? "ui.state.not-cloned" : "ui.state.not-mounted"))}[/]");
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

		var tabs = new List<TabPage>
		{
			new(Texts.Get("ui.tab.repositories"), repos),
			new(Texts.Get("ui.tab.problems"), ProblemsOf(node)),
			new(Texts.Get("ui.tab.versions"), Versions(node)),
			new(Texts.Get("ui.tab.worktrees"), Worktrees(node))
		};
		if (node.Parent is not null)
			tabs.Add(new(Texts.Get("ui.tab.applies"), Applies(node)));
		return new VStack(Title(node), new TabControl(tabs.ToArray())).Spacing(1);
	}

	string State(FxTreeNode repo) => Texts.Get(repo.Kind switch
	{
		FxTreeKind.Declared => repo.Mounted ? "ui.state.not-cloned" : "ui.state.not-mounted",
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

	static Visual Worktrees(FxTreeNode node)
	{
		if (node.Worktrees.Count == 0)
			return new VStack(Line($"[muted]{Esc(Texts.Get("ui.worktrees.none"))}[/]"), Equivalent("fx worktree add <branch>")).Spacing(1);
		var table = new Table().Headers(new TextBlock(Texts.Get("ui.column.branch")), new TextBlock(Texts.Get("ui.column.from")), new TextBlock(Texts.Get("ui.column.repositories")));
		foreach (var worktree in node.Worktrees)
			table.AddRow(new TextBlock(worktree.Card.Branch), new TextBlock(worktree.Card.From), new TextBlock(string.Join(", ", worktree.Card.Worktrees.Select(w => w.Repository))));
		return table;
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
			new TabPage(Texts.Get("ui.tab.problems"), ProblemsOf(node)))).Spacing(1);
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
		else if (node.SupersededBy is null)
			lines.Add(Line(Esc(Texts.Get(node.Mounted ? "ui.not-cloned" : "ui.not-mounted"))));
		if (node.SupersededBy is null)
			lines.Add(Equivalent(node.Mounted ? "fx sync" : $"fx repo mount {node.Name}"));
		return new VStack(lines.ToArray()).Spacing(1);
	}

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

	static Visual FilePanel(FxTreeNode node)
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
