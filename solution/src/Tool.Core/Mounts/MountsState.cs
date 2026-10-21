using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Mounts;

/// <summary>
/// <c>_fx/mounts/~$state.yaml</c>: what this copy has parked. Local, not versioned. The rules (from EnergIA; plan
/// spaces, decision 20): a required mount is always there; a clone that is already there and has no entry is adopted;
/// a new mount without one starts parked; parking never deletes anything.
/// </summary>
public sealed record MountsState(IReadOnlyDictionary<string, bool> Parked)
{
	public static readonly string RelativePath = $"{FxLayout.ModuleFolder(MountsManifest.ModuleName)}/{FxLayout.LocalPrefix}state.yaml";

	public static MountsState Empty { get; } = new(new Dictionary<string, bool>());

	public static MountsState Load(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		if (!FxFiles.Exists(file))
			return Empty;
		YamlNode? document;
		try
		{
			document = YamlDocument.Load(FxFiles.ReadAllText(file));
		}
		catch (YamlSyntaxException)
		{
			return Empty;
		}
		var parked = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		if (document is YamlMapping map && map["mounts"] is YamlSequence list)
			foreach (var item in list.Items.OfType<YamlMapping>())
				if (item["name"] is YamlScalar { Value: { Length: > 0 } mount })
					parked[mount] = item["parked"] is YamlScalar { Value: "true" };
		return new(parked);
	}

	public string ToYaml()
	{
		var b = new StringBuilder(FxHeader.Hash("fx repo mount, fx repo unmount", "fx repo mount <repo>, fx repo unmount <repo>"));
		b.Append($"schema: {FxYamlReader.Schema}\n");
		b.Append("mounts:\n");
		foreach (var (name, parked) in Parked.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
			b.Append($"  - name: {name}\n    parked: {(parked ? "true" : "false")}\n");
		return b.ToString();
	}

	/// <summary>Whether a mount is there: required, the intent of this copy, or adopted if its clone is there.</summary>
	public bool IsMounted(Mount mount, bool cloneExists)
		=> mount.Required || (Parked.TryGetValue(mount.Name, out var parked) ? !parked : cloneExists);

	public MountsState With(string mount, bool mounted)
		=> new(new Dictionary<string, bool>(Parked, StringComparer.OrdinalIgnoreCase) { [mount] = !mounted });
}
