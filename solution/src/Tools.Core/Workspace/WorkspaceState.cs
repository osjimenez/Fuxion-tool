using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>
/// <c>_fx/workspace/~$state.yaml</c>: what this developer has mounted. Local, not versioned. Mounting rules (from
/// EnergIA): a mandatory repository is always mounted; a clone that is already there and has no entry is adopted;
/// unmounting never deletes anything.
/// </summary>
public sealed record WorkspaceState(IReadOnlyDictionary<string, bool> Mount)
{
	public const string RelativePath = "_fx/workspace/~$state.yaml";

	public static WorkspaceState Empty { get; } = new(new Dictionary<string, bool>());

	public static WorkspaceState Load(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		if (!File.Exists(file))
			return Empty;
		YamlNode? document;
		try
		{
			document = YamlDocument.Load(File.ReadAllText(file));
		}
		catch (YamlSyntaxException)
		{
			return Empty;
		}
		var mount = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		if (document is YamlMapping map && map["repositories"] is YamlSequence list)
			foreach (var item in list.Items.OfType<YamlMapping>())
				if (item["name"] is YamlScalar { Value: { Length: > 0 } repo })
					mount[repo] = item["mount"] is not YamlScalar { Value: "false" };
		return new(mount);
	}

	public string ToYaml()
	{
		var b = new StringBuilder(FxHeader.Hash("fx repo mount, fx repo unmount", "fx repo mount <repo>, fx repo unmount <repo>"));
		b.Append($"schema: {FxYamlReader.Schema}\n");
		b.Append("repositories:\n");
		foreach (var (name, mounted) in Mount.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
			b.Append($"  - name: {name}\n    mount: {(mounted ? "true" : "false")}\n");
		return b.ToString();
	}

	/// <summary>Whether a repository is mounted: its policy, the developer's intent, or adopted if its clone is there.</summary>
	public bool IsMounted(WorkspaceRepository repository, bool cloneExists)
		=> repository.Mount == MountPolicy.Mandatory || (Mount.TryGetValue(repository.Name, out var mounted) ? mounted : cloneExists);

	public WorkspaceState With(string repository, bool mounted)
		=> new(new Dictionary<string, bool>(Mount, StringComparer.OrdinalIgnoreCase) { [repository] = mounted });
}
