using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Text;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>
/// A change to a configuration file of fx (<see cref="File"/>, relative to the root): the text before, the editor
/// after, what changed in words and what was found. With an error, it cannot be written; without changes
/// (<see cref="Changed"/>), the info diagnostics say why.
/// </summary>
public sealed record ManifestChange(string File, string Before, YamlEditor After, IReadOnlyList<string> Edits, IReadOnlyList<FxDiagnostic> Diagnostics)
{
	public bool Changed => Before != After.Text;

	public bool Ok => !Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	public IReadOnlyList<DiffHunk> Diff => LineDiff.Compute(Before, After.Text);
}

/// <summary>
/// The commands that edit the configuration of a workspace (plan O, O3.4; plan P): <c>fx repo add|remove|tag|set</c>
/// and <c>fx path add|remove</c> edit <c>_fx/workspace/workspace.yaml</c>; <c>fx module enable|disable</c>, the yaml
/// of the module (creating its folder, or <c>enabled: false</c>). Each changes only what it must and keeps the rest of
/// the file, comments included. The result is validated as the file is read (strict): a change that would leave it
/// invalid has errors and is not written. They never touch the clones nor the folders.
/// </summary>
public static partial class ManifestEditor
{
	public const string RepositoryExists = "workspace.repository-exists";
	public const string RepositoryNeeded = "workspace.repository-needed";
	public const string UnknownKey = "workspace.unknown-key";
	public const string InvalidValue = "workspace.invalid-value";
	public const string UnknownModule = "workspace.unknown-module";
	public const string NotTagged = "workspace.not-tagged";
	public const string CloneKept = "workspace.clone-kept";
	public const string CloneNotMoved = "workspace.clone-not-moved";
	public const string Unchanged = "workspace.unchanged";

	/// <summary>The modules fx knows: a folder of <c>_fx/</c> each.</summary>
	public static readonly IReadOnlyList<string> KnownModules = [WorkspaceModule.Name, DotnetModule.Name];

	/// <summary>The keys of a repository, in the order fx writes them.</summary>
	public static readonly IReadOnlyList<string> RepositoryKeys = ["name", "aliases", "url", "path", "mount", "inherits", "depends-on", "tags"];

	/// <summary>What <see cref="SetRepository"/> changes (the name is the identity; the tags, <see cref="TagRepository"/>).</summary>
	public static readonly IReadOnlyList<string> SettableKeys = ["url", "path", "mount", "inherits", "depends-on"];

	static readonly IReadOnlyList<string> ListKeys = ["depends-on"];

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NotNameChars();

	public static string ManifestPath(string root) => FxLayout.Full(root, WorkspaceManifest.RelativePath);

	/// <summary>The name a repository gets from its URL: the last segment, lowercase, without <c>.git</c> (<c>Fuxion-docs.git</c> → <c>fuxion-docs</c>).</summary>
	public static string NameFromUrl(string url)
	{
		var last = url.TrimEnd('/', '\\').Split('/', '\\', ':').Last();
		if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			last = last[..^4];
		return NotNameChars().Replace(last.ToLowerInvariant(), "-").Trim('-');
	}

	/// <summary>Writes the change (with the byte order mark of the file, if it had one).</summary>
	public static void Write(string root, ManifestChange change)
	{
		var path = FxLayout.Full(root, change.File);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		change.After.WriteFile(path);
	}

	/// <summary>
	/// A new repository at the end of <c>repositories</c>, mounted at <paramref name="path"/> (the layout of the
	/// workspace is the user's: fx has no default for it); the name, by default from the URL. Only what differs from
	/// the defaults is written (manual, inherits).
	/// </summary>
	public static ManifestChange AddRepository(string root, string url, string path, string? name = null, bool mandatory = false,
		IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? tags = null, bool inherits = true)
	{
		var s = new Session(root);
		name ??= NameFromUrl(url);
		path = path.Replace('\\', '/').Trim('/');
		if (s.Current.Resolve($"repositories[name={name}]") is not null)
			return s.Error(RepositoryExists, new FxText(RepositoryExists, name), name);
		var entries = new List<(string, YamlValue)> { ("name", name), ("url", url), ("path", path) };
		if (mandatory)
			entries.Add(("mount", "mandatory"));
		if (!inherits)
			entries.Add(("inherits", new YamlValue.Raw("false")));
		if (dependsOn is { Count: > 0 })
			entries.Add(("depends-on", Flow(dependsOn)));
		if (tags is { Count: > 0 })
			entries.Add(("tags", Flow(tags)));
		s.AppendItem("repositories", new YamlValue.Map(entries.ToArray()));
		return s.Done();
	}

	/// <summary>Takes a repository out of the manifest; its clone stays (and a warning says where).</summary>
	public static ManifestChange RemoveRepository(string root, string name)
	{
		var s = new Session(root);
		if (s.Repository(name) is not { } repository)
			return s.Error(WorkspaceModule.UnknownRepository, new FxText(WorkspaceModule.UnknownRepository, name), name);
		var dependents = s.Manifest.Repositories.Where(r => (r.DependsOn ?? []).Contains(name)).Select(r => r.Name).ToList();
		if (dependents.Count > 0)
			return s.Error(RepositoryNeeded, new FxText(RepositoryNeeded, name, string.Join(", ", dependents)), name);
		s.Apply(s.Current.Remove($"repositories[name={name}]"));
		if (Directory.Exists(s.Manifest.FullPath(repository)))
			s.Diagnostics.Add(FxDiagnostic.Warning(CloneKept, new FxText(CloneKept, name, repository.Path), repository: name));
		return s.Done();
	}

	/// <summary>Adds tags to a repository, or removes them (<paramref name="remove"/>); without tags left, the key goes.</summary>
	public static ManifestChange TagRepository(string root, string name, IReadOnlyList<string> tags, bool remove = false)
	{
		var s = new Session(root);
		if (s.Repository(name) is null)
			return s.Error(WorkspaceModule.UnknownRepository, new FxText(WorkspaceModule.UnknownRepository, name), name);
		var at = $"repositories[name={name}]";
		var current = (s.Current.Resolve($"{at}.tags") as YamlSequence)?.Items.OfType<YamlScalar>().Select(i => i.Value).ToList() ?? [];
		if (!remove)
		{
			var added = tags.Distinct().Where(t => !current.Contains(t)).ToList();
			if (added.Count == 0)
				return s.Info(new FxText($"{Unchanged}.tagged", name, string.Join(", ", tags)), name);
			if (s.Current.Resolve($"{at}.tags") is YamlSequence)
				foreach (var tag in added)
					s.Apply(s.Current.Append($"{at}.tags", tag));
			else
				s.Set(at, "tags", Flow(added));
			return s.Done();
		}
		foreach (var tag in tags.Distinct())
			if (current.Contains(tag))
				s.Apply(s.Current.Remove($"{at}.tags[={tag}]"));
			else
				s.Diagnostics.Add(FxDiagnostic.Warning(NotTagged, new FxText(NotTagged, name, tag), repository: name));
		if (current.Count > 0 && current.All(tags.Contains))
			s.Apply(s.Current.Remove($"{at}.tags"));
		return s.Done();
	}

	/// <summary>
	/// Sets a key of a repository (<see cref="SettableKeys"/>); without values, removes it. <c>mount</c> takes
	/// <c>mandatory</c> or <c>manual</c>, <c>inherits</c> <c>true</c> or <c>false</c>; the lists, several values.
	/// </summary>
	public static ManifestChange SetRepository(string root, string name, string key, IReadOnlyList<string> values)
	{
		var s = new Session(root);
		if (!SettableKeys.Contains(key))
			return s.Error(UnknownKey, new FxText(UnknownKey, key, string.Join(", ", SettableKeys)), name);
		if (s.Repository(name) is not { } repository)
			return s.Error(WorkspaceModule.UnknownRepository, new FxText(WorkspaceModule.UnknownRepository, name), name);
		var at = $"repositories[name={name}]";
		if (values.Count == 0)
		{
			if (s.Current.Resolve($"{at}.{key}") is null)
				return s.Info(new FxText($"{Unchanged}.no-key", name, key), name);
			s.Apply(s.Current.Remove($"{at}.{key}"));
			return s.Done();
		}
		string[]? allowed = key switch
		{
			"mount" => ["mandatory", "manual"],
			"inherits" => ["true", "false"],
			_ => null
		};
		if ((!ListKeys.Contains(key) && values.Count > 1) || (allowed is not null && !allowed.Contains(values[0])))
			return s.Error(InvalidValue, allowed is null
				? new FxText($"{InvalidValue}.single", key, string.Join(" ", values))
				: new FxText(InvalidValue, key, string.Join(" ", values), string.Join(", ", allowed)), name);
		YamlValue value = ListKeys.Contains(key) ? Flow(values) : key == "inherits" ? new YamlValue.Raw(values[0]) : values[0];
		if (value is not YamlValue.Flow && s.Current.Resolve($"{at}.{key}") is YamlScalar)
			s.Apply(s.Current.SetScalar($"{at}.{key}", value));
		else if (value is YamlValue.Flow && s.Current.Resolve($"{at}.{key}") is YamlSequence)
			s.ReplaceList($"{at}.{key}", values);
		else
		{
			if (s.Current.Resolve($"{at}.{key}") is not null)
				s.Apply(s.Current.Remove($"{at}.{key}"));
			s.Set(at, key, value);
		}
		var full = s.Manifest.FullPath(repository);
		if (key == "path" && values[0] != repository.Path && Directory.Exists(full))
			s.Diagnostics.Add(FxDiagnostic.Warning(CloneNotMoved, new FxText(CloneNotMoved, name, repository.Path, values[0]), repository: name));
		return s.Done();
	}

	/// <summary>
	/// Something the git of the workspace versions (<c>paths</c>): a file, or a folder ending in '/'; with
	/// <paramref name="exclude"/>, an exception inside something included; without <paramref name="worktree"/>, left out
	/// of the worktrees. Only the manifest: nothing is created.
	/// </summary>
	public static ManifestChange AddPath(string root, string path, bool exclude = false, bool worktree = true)
	{
		var s = new Session(root);
		path = path.Replace('\\', '/').TrimStart('/');
		if (s.Current.Resolve($"paths[path={path}]") is not null)
			return s.Info(new FxText($"{Unchanged}.path-present", path));
		var entries = new List<(string, YamlValue)> { ("path", path) };
		if (exclude)
			entries.Add(("exclude", new YamlValue.Raw("true")));
		if (!worktree)
			entries.Add(("worktree", new YamlValue.Raw("false")));
		s.AppendItem("paths", new YamlValue.Map(entries.ToArray()));
		return s.Done();
	}

	/// <summary>Takes a path out of <c>paths</c>; what is on disk stays.</summary>
	public static ManifestChange RemovePath(string root, string path)
	{
		var s = new Session(root);
		path = path.Replace('\\', '/').TrimStart('/');
		if (s.Current.Resolve($"paths[path={path}]") is null)
			return s.Info(new FxText($"{Unchanged}.path-absent", path));
		s.Apply(s.Current.Remove($"paths[path={path}]"));
		return s.Done();
	}

	/// <summary>
	/// Enables a module (plan P, decision 2): creates its folder with its yaml, or takes out its <c>enabled: false</c>.
	/// With <paramref name="tags"/>, its <c>applies-to</c> (who it goes down to).
	/// </summary>
	public static ManifestChange EnableModule(string root, string module, IReadOnlyList<string> tags)
	{
		if (!KnownModules.Contains(module))
			return ModuleSession.Unknown(root, module);
		var s = new ModuleSession(root, module);
		var changed = false;
		if (s.Current.Resolve("enabled") is YamlScalar { Value: "false" })
		{
			s.Apply(s.Current.Remove("enabled"));
			changed = true;
		}
		if (tags.Count > 0)
		{
			if (s.Current.Resolve("applies-to.tags") is YamlSequence)
				s.ReplaceList("applies-to.tags", tags);
			else if (s.Current.Resolve("applies-to") is YamlMapping { IsFlow: false } appliesTo && appliesTo.Entries.Count > 0)
				s.Apply(s.Current.AddKey("applies-to", "tags", Flow(tags)));
			else
			{
				if (s.Current.Resolve("applies-to") is not null)
					s.Apply(s.Current.Remove("applies-to"));
				s.Apply(s.Current.AddKey("", "applies-to", new YamlValue.Map(("tags", Flow(tags))), after: s.Current.Resolve("schema") is null ? null : "schema"));
			}
			changed = true;
		}
		if (!changed && !s.Created)
			return s.Info(new FxText($"{Unchanged}.module-enabled", module));
		return s.Done();
	}

	/// <summary>Turns a module off without losing its configuration (<c>enabled: false</c>).</summary>
	public static ManifestChange DisableModule(string root, string module)
	{
		if (!KnownModules.Contains(module))
			return ModuleSession.Unknown(root, module);
		if (!Directory.Exists(FxLayout.Full(root, FxLayout.ModuleFolder(module))))
			return ModuleSession.Info(root, module, new FxText($"{Unchanged}.module-disabled", module));
		var s = new ModuleSession(root, module);
		if (s.Current.Resolve("enabled") is YamlScalar { Value: "false" })
			return s.Info(new FxText($"{Unchanged}.module-disabled", module));
		if (s.Current.Resolve("enabled") is YamlScalar)
			s.Apply(s.Current.SetScalar("enabled", new YamlValue.Raw("false")));
		else
			s.Apply(s.Current.AddKey("", "enabled", new YamlValue.Raw("false"), after: s.Current.Resolve("schema") is null ? null : "schema"));
		return s.Done();
	}

	static YamlValue.Flow Flow(IEnumerable<string> items) => new(items.Select(i => (YamlValue)i).ToArray());

	/// <summary>One edit of a file: the text as it is, the editor as it goes, what was done and found.</summary>
	abstract class Edit(string file, string before, YamlEditor current)
	{
		public string File => file;

		public YamlEditor Current { get; private set; } = current;

		public List<string> Edits { get; } = [];

		public List<FxDiagnostic> Diagnostics { get; } = [];

		public void Apply(YamlEdit edit)
		{
			Current = Current.Apply(edit);
			Edits.Add(edit.What);
		}

		/// <summary>An item at the end of a block list, which is created if there is none (or it is empty, or flow).</summary>
		public void AppendItem(string list, YamlValue item)
		{
			if (Current.Resolve(list) is YamlSequence { IsFlow: false, Items.Count: > 0 })
				Apply(Current.Append(list, item));
			else
			{
				if (Current.Resolve(list) is not null)
					Apply(Current.Remove(list));
				Apply(Current.AddKey("", list, new YamlValue.Block(item)));
			}
		}

		/// <summary>
		/// A list becomes <paramref name="wanted"/>: the new items are appended and the ones that go are removed (in
		/// that order, so it is never empty on the way), so the list keeps its style (flow or block) and the comments of
		/// the items that stay.
		/// </summary>
		public void ReplaceList(string path, IReadOnlyList<string> wanted)
		{
			var items = ((YamlSequence)Current.Resolve(path)!).Items.OfType<YamlScalar>().Select(i => i.Value).ToList();
			foreach (var item in wanted.Distinct().Where(i => !items.Contains(i)))
				Apply(Current.Append(path, item));
			foreach (var item in items.Where(i => !wanted.Contains(i)))
				Apply(Current.Remove($"{path}[={item}]"));
		}

		public ManifestChange Error(string code, FxText text, string? repository = null)
		{
			Diagnostics.Add(FxDiagnostic.Error(code, text, File, repository: repository));
			return Done();
		}

		public ManifestChange Info(FxText text, string? repository = null)
		{
			Diagnostics.Add(FxDiagnostic.Info(Unchanged, text, repository: repository));
			return Done();
		}

		protected abstract void Validate(string text);

		/// <summary>The change, validated as the file is read: an invalid result has the errors, each with its line.</summary>
		public ManifestChange Done()
		{
			if (Current.Text != before && Diagnostics.All(d => d.Severity != FxSeverity.Error))
				Validate(Current.Text);
			return new(File, before, Current, Edits, Diagnostics);
		}
	}

	/// <summary>An edit of <c>_fx/workspace/workspace.yaml</c>.</summary>
	sealed class Session(string root) : Edit(WorkspaceManifest.RelativePath, Text(root), YamlEditor.ReadFile(ManifestPath(root)))
	{
		public WorkspaceManifest Manifest { get; } = WorkspaceManifest.Read(root, System.IO.File.ReadAllText(ManifestPath(root)));

		static string Text(string root) => System.IO.File.ReadAllText(ManifestPath(root));

		public WorkspaceRepository? Repository(string name) => Manifest.Repositories.FirstOrDefault(r => r.Name == name);

		/// <summary>Adds a key to a repository after the last one that goes before it (<see cref="RepositoryKeys"/>).</summary>
		public void Set(string at, string key, YamlValue value)
		{
			var mapping = (YamlMapping)Current.Resolve(at)!;
			var before = RepositoryKeys.TakeWhile(k => k != key).LastOrDefault(k => mapping[k] is not null);
			Apply(Current.AddKey(at, key, value, after: before));
		}

		protected override void Validate(string text)
		{
			try
			{
				WorkspaceManifest.Read(root, text, strict: true);
			}
			catch (WorkspaceManifestException ex)
			{
				Diagnostics.AddRange(ex.Diagnostics);
			}
		}
	}

	/// <summary>An edit of the yaml of a module, created (with its schema) if it does not exist.</summary>
	sealed class ModuleSession : Edit
	{
		readonly string _root;

		public ModuleSession(string root, string module) : this(root, module, FxLayout.ModuleFile(module)) { }

		ModuleSession(string root, string module, string file)
			: base(file, Existing(root, file) ?? "", YamlEditor.Parse(Existing(root, file) ?? $"schema: {FxYamlReader.Schema}\n"))
		{
			_root = root;
			Module = module;
			Created = Existing(root, file) is null;
		}

		public string Module { get; }

		/// <summary>The yaml did not exist: enabling the module creates it.</summary>
		public bool Created { get; }

		static string? Existing(string root, string file) => System.IO.File.Exists(FxLayout.Full(root, file)) ? System.IO.File.ReadAllText(FxLayout.Full(root, file)) : null;

		public static ManifestChange Unknown(string root, string module)
		{
			var s = new ModuleSession(root, KnownModules[0]);
			return s.Error(UnknownModule, new FxText(UnknownModule, module, string.Join(", ", KnownModules)));
		}

		/// <summary>Nothing to change in a module that does not exist (and nothing is created).</summary>
		public static ManifestChange Info(string root, string module, FxText text)
			=> new(FxLayout.ModuleFile(module), "", YamlEditor.Parse(""), [], [FxDiagnostic.Info(Unchanged, text)]);

		protected override void Validate(string text)
		{
			try
			{
				if (Module == DotnetModule.Name)
					DotnetYamlReader.Read(text, File);
				else if (Module == WorkspaceModule.Name)
					WorkspaceManifest.Read(_root, text, strict: true);
			}
			catch (Exception ex) when (ex is DotnetConfigException or WorkspaceManifestException)
			{
				Diagnostics.AddRange(ex is DotnetConfigException d ? d.Diagnostics : ((WorkspaceManifestException)ex).Diagnostics);
			}
		}
	}
}
