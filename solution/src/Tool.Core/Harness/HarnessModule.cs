using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Harness;

/// <summary>
/// The harness of coding agents (plan Q, design §12): the rules and skills of <c>_fx/harness/</c> (and what the root
/// inherits in <c>_fx/.inherited/harness/</c>) become, at the root, the files each agent reads: <c>AGENTS.md</c> (the
/// rule of fx and the rules always read, with the index of the rules by path), <c>CLAUDE.md</c> (<c>@AGENTS.md</c>),
/// <c>.claude/rules/</c> (the rules by path) and the skills in <c>.claude/skills/</c> and <c>.agents/skills/</c>, the one
/// of fx among them. In every root of fx, with or without <c>harness.yaml</c> (proposal 7). Those files and folders are
/// fx's whole (D-28): what is in them and is not fx's is an error, and <c>--adopt</c> moves it to <c>_fx/harness/</c>.
/// </summary>
public static class HarnessModule
{
	public const string Name = "harness";

	/// <summary>The name of fx's own rule and skill, which no rule or skill of the user can take.</summary>
	public const string Reserved = "fx";

	public const string RulesFolder = "rules";
	public const string SkillsFolder = "skills";
	public const string SkillFile = "SKILL.md";

	public const string AgentsFile = "AGENTS.md";
	public const string ClaudeFile = "CLAUDE.md";
	public const string ClaudeRules = ".claude/rules";

	/// <summary>Where the skills go: for Claude, and for the agents of the standard (Codex, Copilot).</summary>
	public static readonly IReadOnlyList<string> SkillRoots = [".claude/skills", ".agents/skills"];

	/// <summary>The files and folders of the agents that are fx's, relative to the root.</summary>
	public static readonly IReadOnlyList<string> Files = [AgentsFile, ClaudeFile];

	public static readonly IReadOnlyList<string> Folders = [ClaudeRules, .. SkillRoots];

	public const string Outdated = "harness.outdated";
	public const string MissingRule = "harness.missing-rule";
	public const string MissingSkill = "harness.missing-skill";
	public const string InvalidSkill = "harness.invalid-skill";
	public const string Undeclared = "harness.undeclared";
	public const string Foreign = "harness.foreign";
	public const string Adopted = "harness.adopted";
	public const string NotAdopted = "harness.not-adopted";

	public static string Folder => FxLayout.ModuleFolder(Name);
	public static string ConfigFile => FxLayout.ModuleFile(Name);
	public static string InheritedFolder => FxLayout.InheritedModuleFolder(Name);
	public static string InheritedConfigFile => $"{InheritedFolder}/{Name}.yaml";

	/// <summary>
	/// The configuration of <paramref name="node"/> (who is <paramref name="identity"/>): what it inherits, if the module is
	/// for it, with its own on top, only what is for it (decision 3); where it comes from; and its own, whole.
	/// </summary>
	public static (HarnessConfig Config, string Sources, HarnessConfig Own) Configuration(FxNode node, FxIdentity identity, ICollection<FxDiagnostic> warnings)
	{
		var inheritedFile = node.Full(InheritedConfigFile);
		var ownFile = node.Full(ConfigFile);
		var inherited = FxFiles.Exists(inheritedFile) ? HarnessYamlReader.ReadFile(inheritedFile, warnings) : null;
		var own = FxFiles.Exists(ownFile) ? HarnessYamlReader.ReadFile(ownFile, warnings).Absolute(identity.FullName) : null;
		if (own is { Enabled: false })
			own = null;
		// a workspace receives what comes from above to pass it on; it only uses it if it is for it
		if (inherited is not null && !(inherited.AppliesTo ?? FxAppliesTo.All).Matches(identity))
			inherited = null;
		var sources = string.Join(", ", new[] { inherited is null ? null : InheritedConfigFile, own is null ? null : ConfigFile }.OfType<string>());
		var config = HarnessConfig.Combine(inherited?.WithBase(InheritedFolder) ?? HarnessConfig.Empty, own?.WithBase(Folder) ?? HarnessConfig.Empty).ForTarget(identity);
		return (config, sources, own?.WithBase(Folder) ?? HarnessConfig.Empty);
	}

	/// <summary>The module in <paramref name="node"/>, written or checked; null if it is not a root of fx.</summary>
	public static FxModuleResult? Run(FxNode node, FxIdentity identity, string? repository, SyncContext context)
	{
		if (!FxFiles.DirectoryExists(node.Full(FxLayout.Folder)))
			return null;
		var diagnostics = new FxDiagnostics(context.Progress, repository);
		var outputs = new FxOutputs(context.DisplayRoot, context.Write, context.Adopt, diagnostics);
		context.Progress.Step(Name, repository, context.Doctor ? "check" : "generate", () =>
		{
			Generate(node, identity, context, outputs, diagnostics);
			return true;
		});
		if (context.Doctor)
			foreach (var file in outputs.Files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(Outdated, new(Outdated), file.Path, fix: FxFix.Run(FxHeader.Sync)));
		return new(Name, context.DisplayRoot, repository, outputs.Files, [], diagnostics.ToList()) { Node = node.Root };
	}

	static void Generate(FxNode node, FxIdentity identity, SyncContext context, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		// 0. With --adopt, what is not fx's in the files of the agents becomes rules and skills of _fx/harness/
		var adopted = context.Adopt ? Adopt(node, outputs, diagnostics) : [];

		// 1. The configuration
		(HarnessConfig Config, string Sources, HarnessConfig Own) configuration;
		var warnings = new List<FxDiagnostic>();
		try
		{
			configuration = Configuration(node, identity, warnings);
		}
		catch (HarnessConfigException ex)
		{
			diagnostics.AddRange(ex.Diagnostics.Select(d => d with { File = d.File is null ? null : outputs.Display(d.File) }));
			return;
		}
		diagnostics.AddRange(warnings.Select(d => d with { File = d.File is null ? null : outputs.Display(d.File) }));
		var (config, sources, own) = configuration;
		var from = sources.Length == 0 ? "fx" : sources;
		Check(node, own, context.Doctor, outputs, diagnostics);

		// 2. The rules: AGENTS.md (the rule of fx first), CLAUDE.md and the rules by path
		var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void Write(string relative, string content)
		{
			expected.Add(relative);
			outputs.Generated(node.Full(relative), content);
		}
		var rules = config.Rules.Where(r => FxFiles.Exists(node.Full(r.File))).Select(r => (Rule: r, Text: FxFiles.ReadAllText(node.Full(r.File)))).ToList();
		var fxRule = HarnessGenerator.FxRule(identity, node.IsWorkspace, node.Inherited()?.Parent);
		Write(AgentsFile, HarnessGenerator.Agents(from, fxRule, rules));
		Write(ClaudeFile, HarnessGenerator.Claude(from));
		foreach (var (rule, text) in rules.Where(r => r.Rule.ByPath))
			Write(HarnessGenerator.ClaudeRule(rule.Name), HarnessGenerator.PathRule(rule, text, rule.File));

		// 3. The skills, in each folder of skills: the one of fx (made by the CLI from its commands) and the declared ones
		var skills = new List<(string Name, string Text, IReadOnlyList<(string Relative, byte[] Content)> Files)>();
		if (context.Skill is not null)
			skills.Add((Reserved, context.Skill, []));
		foreach (var skill in config.Skills)
		{
			var folder = node.Full(skill.Folder);
			var file = Path.Combine(folder, SkillFile);
			if (!FxFiles.Exists(file) || HarnessGenerator.Skill(FxFiles.ReadAllText(file), skill.Folder) is not { } text || SkillProblem(FxFiles.ReadAllText(file), skill.Name) is not null)
				continue;
			var files = FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
				.Select(f => FxLayout.Relative(folder, f))
				.Where(r => r != SkillFile && !r.Split('/').Any(FxLayout.IsLocal))
				.Order(StringComparer.Ordinal)
				.Select(r => (r, FxFiles.ReadAllBytes(Path.Combine(folder, r))))
				.ToList();
			skills.Add((skill.Name, text, files));
		}
		var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in SkillRoots)
			foreach (var (name, text, files) in skills)
			{
				var folder = $"{root}/{name}";
				var before = outputs.Files.Count;
				Write($"{folder}/{SkillFile}", text);
				// one of the user's (without the header): not overwritten, and nothing of it is touched
				if (outputs.Files[before].Status == SyncFileStatus.Refused)
				{
					refused.Add(folder);
					continue;
				}
				foreach (var (relative, content) in files)
				{
					expected.Add($"{folder}/{relative}");
					outputs.Owned(node.Full($"{folder}/{relative}"), content);
				}
			}

		// 4. What is there and is no longer wanted: fx's goes, the user's is an error (or was just adopted)
		Clean(node, expected, refused, adopted, keepFx: context.Skill is null, outputs, diagnostics);
	}

	/// <summary>
	/// The rules and skills of the root itself, whoever they are for: their files must be there, and the skills need a
	/// valid <c>SKILL.md</c>. In the doctor, also what is in <c>_fx/harness/</c> and goes nowhere.
	/// </summary>
	static void Check(FxNode node, HarnessConfig own, bool doctor, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var yaml = outputs.Display(node.Full(ConfigFile));
		foreach (var rule in own.Rules.Where(r => !FxFiles.Exists(node.Full(r.File))))
			diagnostics.Add(FxDiagnostic.Error(MissingRule, new(MissingRule, rule.Name, rule.File), yaml, rule.Line));
		foreach (var skill in own.Skills)
		{
			var file = node.Full($"{skill.Folder}/{SkillFile}");
			if (!FxFiles.Exists(file))
				diagnostics.Add(FxDiagnostic.Error(MissingSkill, new(MissingSkill, skill.Name, $"{skill.Folder}/{SkillFile}"), yaml, skill.Line));
			else if (SkillProblem(FxFiles.ReadAllText(file), skill.Name) is { } problem)
				diagnostics.Add(FxDiagnostic.Error(InvalidSkill, new($"{InvalidSkill}.{problem}", skill.Name), outputs.Display(file)));
		}
		if (!doctor)
			return;
		var rulesFolder = node.Full($"{Folder}/{RulesFolder}");
		if (FxFiles.DirectoryExists(rulesFolder))
			foreach (var file in FxFiles.EnumerateFiles(rulesFolder, "*", SearchOption.AllDirectories).Where(f => !FxLayout.IsLocal(Path.GetFileName(f))))
			{
				var relative = FxLayout.Relative(rulesFolder, file);
				if (!own.Rules.Any(r => string.Equals($"{r.Name}.md", relative, StringComparison.OrdinalIgnoreCase)))
					diagnostics.Add(FxDiagnostic.Warning(Undeclared, new(Undeclared, $"{Folder}/{RulesFolder}/{relative}"), outputs.Display(file), fix: FxFix.Do($"{Undeclared}.fix")));
			}
		var skillsFolder = node.Full($"{Folder}/{SkillsFolder}");
		if (FxFiles.DirectoryExists(skillsFolder))
			foreach (var folder in FxFiles.EnumerateDirectories(skillsFolder).Where(d => !FxLayout.IsLocal(Path.GetFileName(d))))
				if (!own.Skills.Any(s => string.Equals(s.Name, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
					diagnostics.Add(FxDiagnostic.Warning(Undeclared, new(Undeclared, $"{Folder}/{SkillsFolder}/{Path.GetFileName(folder)}/"), outputs.Display(folder),
						fix: FxFix.Do($"{Undeclared}.fix")));
	}

	/// <summary>
	/// What is wrong with a <c>SKILL.md</c> (its front matter, with <c>name</c> as its folder and a <c>description</c>), as
	/// the variant of the text of <see cref="InvalidSkill"/>; null if nothing.
	/// </summary>
	public static string? SkillProblem(string text, string name)
	{
		if (HarnessGenerator.FrontMatter(text) is not { } parts)
			return "no-front-matter";
		YamlMapping? map;
		try
		{
			map = YamlDocument.Load(parts.Yaml) as YamlMapping;
		}
		catch (YamlSyntaxException)
		{
			return "front-matter";
		}
		if (map is null)
			return "front-matter";
		if (!string.Equals(FxYamlReader.Scalar(map["name"]), name, StringComparison.Ordinal))
			return "name";
		return string.IsNullOrWhiteSpace(FxYamlReader.Scalar(map["description"])) ? "description" : null;
	}

	/// <summary>
	/// In the files and folders of the agents, what fx did not generate in this run: fx's (with the header) goes; the user's
	/// is an error, unless it was just adopted. A skill of the user that was refused is left as it is, and so is the skill
	/// of fx when this run cannot make it (<paramref name="keepFx"/>: no CLI).
	/// </summary>
	static void Clean(FxNode node, ISet<string> expected, ISet<string> refused, ISet<string> adopted, bool keepFx, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		void NotOurs(string relative) => diagnostics.Add(FxDiagnostic.Error(Foreign, new(Foreign, relative), outputs.Display(node.Full(relative)),
			fix: new FxFix("fx sync --adopt", new($"{Foreign}.fix"))));

		var rules = node.Full(ClaudeRules);
		if (FxFiles.DirectoryExists(rules))
		{
			foreach (var file in FxFiles.EnumerateFiles(rules, "*", SearchOption.AllDirectories))
			{
				var relative = FxLayout.Relative(node.Root, file);
				if (expected.Contains(relative))
					continue;
				if (adopted.Contains(relative) || FxHeader.IsGenerated(FxFiles.ReadAllText(file)))
					outputs.Delete(file);
				else
					NotOurs(relative);
			}
			outputs.Prune(rules);
		}

		foreach (var root in SkillRoots)
		{
			var folder = node.Full(root);
			if (!FxFiles.DirectoryExists(folder))
				continue;
			foreach (var file in FxFiles.EnumerateFiles(folder).Where(f => !expected.Contains(FxLayout.Relative(node.Root, f))))
				NotOurs(FxLayout.Relative(node.Root, file));
			foreach (var skill in FxFiles.EnumerateDirectories(folder))
			{
				var relative = FxLayout.Relative(node.Root, skill);
				if (refused.Contains(relative) || (keepFx && string.Equals(Path.GetFileName(skill), Reserved, StringComparison.OrdinalIgnoreCase)))
					continue;
				var files = FxFiles.EnumerateFiles(skill, "*", SearchOption.AllDirectories).ToList();
				var skillFile = Path.Combine(skill, SkillFile);
				var ours = expected.Contains($"{relative}/{SkillFile}") || adopted.Contains(relative)
				           || (FxFiles.Exists(skillFile) && FxHeader.IsGenerated(FxFiles.ReadAllText(skillFile)));
				if (!ours)
				{
					NotOurs(relative + "/");
					continue;
				}
				foreach (var file in files.Where(f => !expected.Contains(FxLayout.Relative(node.Root, f))))
					outputs.Delete(file);
			}
			outputs.Prune(folder);
		}
	}

	/// <summary>
	/// <c>--adopt</c> (D-28, proposal 8): an <c>AGENTS.md</c> or <c>CLAUDE.md</c> written by hand becomes the rule
	/// <c>agents-adopted</c> or <c>claude-adopted</c>, a rule of <c>.claude/rules/</c> a rule with its <c>paths</c>, and a
	/// skill a skill of <c>_fx/harness/skills/</c>; all of them declared in <c>harness.yaml</c>. Returns what was adopted,
	/// relative to the root, for the cleaning to remove what does not come back.
	/// </summary>
	static HashSet<string> Adopt(FxNode node, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var ownFile = node.Full(ConfigFile);
		HarnessConfig own;
		try
		{
			own = FxFiles.Exists(ownFile) ? HarnessYamlReader.ReadFile(ownFile) : HarnessConfig.Empty;
		}
		catch (HarnessConfigException)
		{
			// the configuration says what is wrong with it; nothing is adopted into a yaml that cannot be read
			return adopted;
		}
		var rules = new List<(string Name, IReadOnlyList<string> Paths, string Text, string From)>();
		var skills = new List<(string Name, string Folder)>();
		bool Taken(string name, bool skill) => skill
			? own.Skills.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) || skills.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
			  || FxFiles.DirectoryExists(node.Full($"{Folder}/{SkillsFolder}/{name}"))
			: own.Rules.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) || rules.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
			  || FxFiles.Exists(node.Full($"{Folder}/{RulesFolder}/{name}.md"));
		void Refuse(string relative, string name) => diagnostics.Add(FxDiagnostic.Error(NotAdopted, new(NotAdopted, relative, name), outputs.Display(node.Full(relative))));

		foreach (var (file, name) in new[] { (AgentsFile, "agents-adopted"), (ClaudeFile, "claude-adopted") })
		{
			var path = node.Full(file);
			if (!FxFiles.Exists(path) || FxHeader.IsGenerated(FxFiles.ReadAllText(path)))
				continue;
			var text = FxFiles.ReadAllText(path);
			// a CLAUDE.md that only reads AGENTS.md says nothing of its own
			if (file == ClaudeFile && text.Trim() == "@AGENTS.md")
				continue;
			if (Taken(name, skill: false))
				Refuse(file, name);
			else
				rules.Add((name, [], text, file));
		}
		var claudeRules = node.Full(ClaudeRules);
		if (FxFiles.DirectoryExists(claudeRules))
			foreach (var path in FxFiles.EnumerateFiles(claudeRules, "*.md"))
			{
				var text = FxFiles.ReadAllText(path);
				if (FxHeader.IsGenerated(text))
					continue;
				var relative = FxLayout.Relative(node.Root, path);
				var name = Path.GetFileNameWithoutExtension(path);
				IReadOnlyList<string> paths = [];
				if (HarnessGenerator.FrontMatter(text) is { } parts)
				{
					try
					{
						paths = YamlDocument.Load(parts.Yaml) is YamlMapping map ? FxYamlReader.List(map["paths"]) : [];
						text = parts.Body;
					}
					catch (YamlSyntaxException)
					{
						// a front matter that cannot be read stays in the text of the rule
					}
				}
				if (string.Equals(name, Reserved, StringComparison.OrdinalIgnoreCase) || Taken(name, skill: false))
					Refuse(relative, name);
				else
					rules.Add((name, paths, text, relative));
			}
		foreach (var root in SkillRoots)
		{
			var folder = node.Full(root);
			if (!FxFiles.DirectoryExists(folder))
				continue;
			foreach (var skill in FxFiles.EnumerateDirectories(folder))
			{
				var skillFile = Path.Combine(skill, SkillFile);
				if (FxFiles.Exists(skillFile) && FxHeader.IsGenerated(FxFiles.ReadAllText(skillFile)))
					continue;
				var relative = FxLayout.Relative(node.Root, skill);
				var name = Path.GetFileName(skill);
				// the same skill in both folders: the first one is taken, the other one is generated again
				if (skills.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
				{
					adopted.Add(relative);
					continue;
				}
				if (string.Equals(name, Reserved, StringComparison.OrdinalIgnoreCase) || Taken(name, skill: true))
					Refuse(relative + "/", name);
				else
					skills.Add((name, skill));
			}
		}
		if (rules.Count + skills.Count == 0)
			return adopted;

		// declared in harness.yaml (created if it is not there), or nothing is adopted
		var editor = YamlEditor.Parse(FxFiles.Exists(ownFile) ? FxFiles.ReadAllText(ownFile) : $"schema: {FxYamlReader.Schema}\n");
		try
		{
			void Add(string key, YamlValue item)
				=> editor = editor.Resolve(key) is YamlSequence
					? editor.Apply(editor.Append(key, item))
					: editor.Apply(editor.AddKey("", key, new YamlValue.Block(item)));
			foreach (var (name, paths, _, _) in rules)
				Add("rules", paths.Count == 0
					? new YamlValue.Map(("name", name))
					: new YamlValue.Map(("name", name), ("paths", new YamlValue.Flow(paths.Select(p => (YamlValue)p).ToArray()))));
			foreach (var (name, _) in skills)
				Add("skills", new YamlValue.Map(("name", name)));
		}
		catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
		{
			diagnostics.Add(FxDiagnostic.Error(NotAdopted, new($"{NotAdopted}.yaml", ex.Message), outputs.Display(ownFile)));
			return adopted;
		}
		outputs.Owned(ownFile, Encoding.UTF8.GetBytes(editor.Text));

		// the sources, in _fx/harness/
		foreach (var (name, _, text, fromFile) in rules)
		{
			outputs.Owned(node.Full($"{Folder}/{RulesFolder}/{name}.md"), Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Trim('\n') + "\n"));
			adopted.Add(fromFile);
			diagnostics.Add(FxDiagnostic.Info(Adopted, new($"{Adopted}.rule", fromFile, name), outputs.Display(node.Full(fromFile))));
		}
		foreach (var (name, folder) in skills)
		{
			foreach (var file in FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
				outputs.Owned(node.Full($"{Folder}/{SkillsFolder}/{name}/{FxLayout.Relative(folder, file)}"), FxFiles.ReadAllBytes(file));
			var relative = FxLayout.Relative(node.Root, folder);
			adopted.Add(relative);
			diagnostics.Add(FxDiagnostic.Info(Adopted, new($"{Adopted}.skill", relative + "/", name), outputs.Display(folder)));
		}
		return adopted;
	}
}
