using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Text;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>
/// A change to the manifest: the text before, the editor after, what changed in words and what was found. With an
/// error, it cannot be written; without changes (<see cref="Changed"/>), the info diagnostics say why.
/// </summary>
public sealed record ManifestChange(string Before, YamlEditor After, IReadOnlyList<string> Edits, IReadOnlyList<FxDiagnostic> Diagnostics)
{
	public bool Changed => Before != After.Text;

	public bool Ok => !Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	public IReadOnlyList<DiffHunk> Diff => LineDiff.Compute(Before, After.Text);
}

/// <summary>
/// The commands that edit the manifest (plan O, O3.4: <c>fx repo add|remove|tag|set</c>, <c>fx module
/// enable|disable</c>, <c>fx folder add|remove</c>), on the edit engine of O2.2: each changes only what it must and
/// keeps the rest of the file, comments included. The result is validated as the manifest is read (strict): a change
/// that would leave it invalid has errors and is not written. They never touch the clones nor the folders.
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

	/// <summary>The keys of a repository, in the order fx writes them.</summary>
	public static readonly IReadOnlyList<string> RepositoryKeys = ["name", "path", "url", "mount", "standalone", "dependsOn", "solution", "solutionExclude", "tags"];

	/// <summary>What <see cref="SetRepository"/> changes (the name is the identity; the tags, <see cref="TagRepository"/>).</summary>
	public static readonly IReadOnlyList<string> SettableKeys = ["url", "path", "mount", "standalone", "dependsOn", "solution", "solutionExclude"];

	static readonly IReadOnlyList<string> ListKeys = ["dependsOn", "solutionExclude"];

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NotNameChars();

	public static string ManifestPath(string root) => Path.Combine(root, "_fx", "workspace.yaml");

	/// <summary>The name a repository gets from its URL: the last segment, lowercase, without <c>.git</c> (<c>Fuxion-docs.git</c> → <c>fuxion-docs</c>).</summary>
	public static string NameFromUrl(string url)
	{
		var last = url.TrimEnd('/', '\\').Split('/', '\\', ':').Last();
		if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			last = last[..^4];
		return NotNameChars().Replace(last.ToLowerInvariant(), "-").Trim('-');
	}

	/// <summary>Writes the change (with the byte order mark of the file, if it had one).</summary>
	public static void Write(string root, ManifestChange change) => change.After.WriteFile(ManifestPath(root));

	/// <summary>
	/// A new repository at the end of <c>repositories</c>, mounted at <paramref name="path"/> (the layout of the
	/// workspace is the user's: fx has no default for it); the name, by default from the URL. Only what differs from
	/// the defaults is written (manual, not standalone).
	/// </summary>
	public static ManifestChange AddRepository(string root, string url, string path, string? name = null, bool mandatory = false, bool standalone = false,
		string? solution = null, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? tags = null)
	{
		var s = new Session(root);
		name ??= NameFromUrl(url);
		path = path.Replace('\\', '/').Trim('/');
		if (s.Current.Resolve($"repositories[name={name}]") is not null)
			return s.Error(RepositoryExists, new FxText(RepositoryExists, name), name);
		var entries = new List<(string, YamlValue)> { ("name", name), ("path", path), ("url", url) };
		if (mandatory)
			entries.Add(("mount", "mandatory"));
		if (standalone)
			entries.Add(("standalone", new YamlValue.Raw("true")));
		if (dependsOn is { Count: > 0 })
			entries.Add(("dependsOn", Flow(dependsOn)));
		if (solution is not null)
			entries.Add(("solution", solution));
		if (tags is { Count: > 0 })
			entries.Add(("tags", Flow(tags)));
		var repository = new YamlValue.Map(entries.ToArray());
		if (s.Current.Resolve("repositories") is YamlSequence { IsFlow: false })
			s.Apply(s.Current.Append("repositories", repository));
		else
		{
			// none yet (no key, empty, or [] left by the last removal): a block list
			if (s.Current.Resolve("repositories") is not null)
				s.Apply(s.Current.Remove("repositories"));
			s.Apply(s.Current.AddKey("", "repositories", new YamlValue.Block(repository)));
		}
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
	/// <c>mandatory</c> or <c>manual</c>, <c>standalone</c> <c>true</c> or <c>false</c>; the lists, several values.
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
			"standalone" => ["true", "false"],
			_ => null
		};
		if ((!ListKeys.Contains(key) && values.Count > 1) || (allowed is not null && !allowed.Contains(values[0])))
			return s.Error(InvalidValue, allowed is null
				? new FxText($"{InvalidValue}.single", key, string.Join(" ", values))
				: new FxText(InvalidValue, key, string.Join(" ", values), string.Join(", ", allowed)), name);
		YamlValue value = ListKeys.Contains(key) ? Flow(values) : key == "standalone" ? new YamlValue.Raw(values[0]) : values[0];
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

	/// <summary>Enables a module of fx for the repositories with these tags (for every repository, without tags); on an enabled one, changes its tags.</summary>
	public static ManifestChange EnableModule(string root, string module, IReadOnlyList<string> tags)
	{
		var s = new Session(root);
		if (!WorkspaceManifest.KnownModules.Contains(module))
			return s.Error(UnknownModule, new FxText(UnknownModule, module, string.Join(", ", WorkspaceManifest.KnownModules)));
		YamlValue settings = tags.Count > 0 ? new YamlValue.Map(("tags", Flow(tags))) : new YamlValue.Map();
		var existing = s.Current.Resolve($"modules.{module}");
		if (existing is YamlMapping current)
		{
			if (tags.Count == 0)
				return s.Info(new FxText($"{Unchanged}.module-enabled", module));
			if (current["tags"] is YamlSequence)
				s.ReplaceList($"modules.{module}.tags", tags);
			else
			{
				if (current["tags"] is not null)
					s.Apply(s.Current.Remove($"modules.{module}.tags"));
				s.Apply(s.Current.AddKey($"modules.{module}", "tags", Flow(tags)));
			}
		}
		else if (existing is null && s.Current.Resolve("modules") is YamlMapping { IsFlow: false })
			s.Apply(s.Current.AddKey("modules", module, settings));
		else
		{
			// no modules yet, or a module with no settings (dotnet:): written again
			if (existing is not null)
				s.Apply(s.Current.Remove($"modules.{module}"));
			if (s.Current.Resolve("modules") is YamlMapping { IsFlow: false } modules && modules.Entries.Count > 0)
				s.Apply(s.Current.AddKey("modules", module, settings));
			else
			{
				if (s.Current.Resolve("modules") is not null)
					s.Apply(s.Current.Remove("modules"));
				s.Apply(s.Current.AddKey("", "modules", new YamlValue.Map((module, settings)), after: s.LastOf("workspace", "folders")));
			}
		}
		return s.Done();
	}

	/// <summary>Disables a module; with the last one, <c>modules</c> goes too.</summary>
	public static ManifestChange DisableModule(string root, string module)
	{
		var s = new Session(root);
		if (s.Current.Resolve($"modules.{module}") is null)
			return s.Info(new FxText($"{Unchanged}.module-disabled", module));
		s.Apply(s.Current.Resolve("modules") is YamlMapping { Entries.Count: 1 } ? s.Current.Remove("modules") : s.Current.Remove($"modules.{module}"));
		return s.Done();
	}

	/// <summary>A folder of the metarepo (<c>folders</c>): only the manifest, the folder is not created.</summary>
	public static ManifestChange AddFolder(string root, string folder)
	{
		var s = new Session(root);
		if (s.Current.Resolve($"folders[={folder}]") is not null)
			return s.Info(new FxText($"{Unchanged}.folder-present", folder));
		if (s.Current.Resolve("folders") is YamlSequence)
			s.Apply(s.Current.Append("folders", folder));
		else
		{
			if (s.Current.Resolve("folders") is not null)
				s.Apply(s.Current.Remove("folders"));
			s.Apply(s.Current.AddKey("", "folders", Flow([folder]), after: s.LastOf("workspace")));
		}
		return s.Done();
	}

	/// <summary>Takes a folder out of <c>folders</c>; the folder stays.</summary>
	public static ManifestChange RemoveFolder(string root, string folder)
	{
		var s = new Session(root);
		if (s.Current.Resolve($"folders[={folder}]") is null)
			return s.Info(new FxText($"{Unchanged}.folder-absent", folder));
		s.Apply(s.Current.Remove($"folders[={folder}]"));
		return s.Done();
	}

	static YamlValue.Flow Flow(IEnumerable<string> items) => new(items.Select(i => (YamlValue)i).ToArray());

	/// <summary>One edit of the manifest: the file as it is, the editor as it goes, what was done and found.</summary>
	sealed class Session(string root)
	{
		readonly string _before = File.ReadAllText(ManifestPath(root));

		public YamlEditor Current { get; private set; } = YamlEditor.ReadFile(ManifestPath(root));

		public WorkspaceManifest Manifest { get; } = WorkspaceManifest.Read(root, File.ReadAllText(ManifestPath(root)));

		public List<string> Edits { get; } = [];

		public List<FxDiagnostic> Diagnostics { get; } = [];

		public WorkspaceRepository? Repository(string name) => Manifest.Repositories.FirstOrDefault(r => r.Name == name);

		public void Apply(YamlEdit edit)
		{
			Current = Current.Apply(edit);
			Edits.Add(edit.What);
		}

		/// <summary>Adds a key to a repository after the last one that goes before it (<see cref="RepositoryKeys"/>).</summary>
		public void Set(string at, string key, YamlValue value)
		{
			var mapping = (YamlMapping)Current.Resolve(at)!;
			var before = RepositoryKeys.TakeWhile(k => k != key).LastOrDefault(k => mapping[k] is not null);
			Apply(Current.AddKey(at, key, value, after: before));
		}

		/// <summary>
		/// A list becomes <paramref name="wanted"/>: the items that go are removed and the new ones appended, so the
		/// list keeps its style (flow or block) and the comments of the items that stay.
		/// </summary>
		public void ReplaceList(string path, IReadOnlyList<string> wanted)
		{
			var current = ((YamlSequence)Current.Resolve(path)!).Items.OfType<YamlScalar>().Select(i => i.Value).ToList();
			foreach (var item in current.Where(i => !wanted.Contains(i)))
				Apply(Current.Remove($"{path}[={item}]"));
			foreach (var item in wanted.Distinct().Where(i => !current.Contains(i)))
				Apply(Current.Append(path, item));
		}

		/// <summary>The last of these root keys that the manifest has, to add another after it (at the end, if none).</summary>
		public string? LastOf(params string[] keys) => keys.LastOrDefault(k => Current.Resolve(k) is not null);

		public ManifestChange Error(string code, FxText text, string? repository = null)
		{
			Diagnostics.Add(FxDiagnostic.Error(code, text, WorkspaceManifest.RelativePath, repository: repository));
			return Done();
		}

		public ManifestChange Info(FxText text, string? repository = null)
		{
			Diagnostics.Add(FxDiagnostic.Info(Unchanged, text, repository: repository));
			return Done();
		}

		/// <summary>The change, validated as the manifest is read: an invalid result has the errors, each with its line.</summary>
		public ManifestChange Done()
		{
			if (Current.Text != _before && Diagnostics.All(d => d.Severity != FxSeverity.Error))
				try
				{
					WorkspaceManifest.Read(root, Current.Text, strict: true);
				}
				catch (WorkspaceManifestException ex)
				{
					Diagnostics.AddRange(ex.Diagnostics);
				}
			return new(_before, Current, Edits, Diagnostics);
		}
	}
}
