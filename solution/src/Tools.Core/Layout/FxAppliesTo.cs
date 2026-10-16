using System;
using System.Collections.Generic;
using System.Linq;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Layout;

/// <summary>
/// Who a repository (or a workspace) is: its name, given by whoever declares it; its full name, the chain of names from
/// the root (<c>/acme/repo-w1/repo-c1</c>, plan P, decision 11); its tags (decision 21) and its old names (decision 12).
/// </summary>
public sealed record FxIdentity(string Name, string FullName, IReadOnlyList<string> Tags, IReadOnlyList<string>? Aliases = null)
{
	/// <summary>Whether <paramref name="fullName"/> (absolute) is this one, by its name or an old one.</summary>
	public bool Is(string fullName)
		=> string.Equals(FullName, fullName, StringComparison.OrdinalIgnoreCase)
		   || (Aliases ?? []).Any(a => string.Equals(a, fullName, StringComparison.OrdinalIgnoreCase));

	/// <summary>The identity of a root nobody declares: its own name, without tags.</summary>
	public static FxIdentity Root(string name) => new(name, "/" + name, []);

	/// <summary>
	/// A reference to a repository written by <paramref name="writer"/> (its full name), made absolute: with '/' in
	/// front it already is; without it, it is relative to the workspace that wrote it (decision 11).
	/// </summary>
	public static string Resolve(string reference, string writer)
		=> reference.StartsWith('/') ? reference.TrimEnd('/') : $"{writer.TrimEnd('/')}/{reference.Trim('/')}";
}

/// <summary>
/// Who something is for (plan P, decision 22): a module, an import, a variable, a package, a share. By tags or by
/// repositories (names: absolute with '/', relative to the workspace that writes them without it); without both, for
/// everyone. A workspace in between always receives what comes from above, to pass it on: <c>applies-to</c> says who
/// uses something, never who lets it through.
/// </summary>
public sealed record FxAppliesTo(IReadOnlyList<string> Tags, IReadOnlyList<string> Repositories)
{
	public static FxAppliesTo All { get; } = new([], []);

	public bool IsAll => Tags.Count == 0 && Repositories.Count == 0;

	/// <summary>Whether it is for <paramref name="who"/> (the repositories, already absolute: <see cref="Absolute"/>).</summary>
	public bool Matches(FxIdentity who)
		=> IsAll
		   || Tags.Any(t => who.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
		   || Repositories.Any(who.Is);

	/// <summary>
	/// Whether it may be for the workspace <paramref name="who"/> or for something below it, so the workspace passes it
	/// on: by tags (its repositories have tags its parent does not know), for everyone, or naming it or a repository
	/// below it (plan Q, proposal 1; plan R, decision 2).
	/// </summary>
	public bool MayApplyBelow(FxIdentity who)
		=> IsAll || Tags.Count > 0 || Repositories.Any(r => who.Is(r)
			|| new[] { who.FullName }.Concat(who.Aliases ?? []).Any(n => r.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)));

	/// <summary>The repositories made absolute, written by <paramref name="writer"/>.</summary>
	public FxAppliesTo Absolute(string writer) => this with { Repositories = Repositories.Select(r => FxIdentity.Resolve(r, writer)).ToList() };

	/// <summary><c>applies-to</c> of <paramref name="map"/>, or null if it has none.</summary>
	public static FxAppliesTo? Read(YamlMapping map, FxYamlReader reader)
	{
		if (map["applies-to"] is not { } node)
			return null;
		if (node is not YamlMapping appliesTo)
		{
			reader.Error(node, "applies-to");
			return null;
		}
		reader.Keys(appliesTo, "tags", "repositories");
		return new(FxYamlReader.List(appliesTo["tags"]), FxYamlReader.List(appliesTo["repositories"]));
	}

	/// <summary>As YAML, for the files fx writes; null for everyone.</summary>
	public YamlValue? ToYaml()
	{
		if (IsAll)
			return null;
		var entries = new List<(string, YamlValue)>();
		if (Tags.Count > 0)
			entries.Add(("tags", new YamlValue.Flow(Tags.Select(t => (YamlValue)t).ToArray())));
		if (Repositories.Count > 0)
			entries.Add(("repositories", new YamlValue.Flow(Repositories.Select(r => (YamlValue)r).ToArray())));
		return new YamlValue.Map(entries.ToArray());
	}
}
