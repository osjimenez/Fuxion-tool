using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>A file a share gives: where it goes in the repository that receives it, its content and the workspace it comes from.</summary>
public sealed record SharedFile(string Destination, byte[] Content, string From);

/// <summary>A share on its way down: its entry (<c>applies-to</c> already absolute) and its files; none if its source is not there.</summary>
public sealed record ShareFlow(WorkspaceShare Share, IReadOnlyList<SharedFile>? Files);

/// <summary>What a repository receives of the shares: its files by destination, and the destinations whose source is missing (kept as they are).</summary>
public sealed record ReceivedShares(IReadOnlyDictionary<string, SharedFile> Files, IReadOnlyList<string> Kept)
{
	public bool Any => Files.Count > 0 || Kept.Count > 0;
}

/// <summary>
/// The shares (plan P, decision 23): files a workspace gives the repositories below it, outside <c>_fx/</c>, at their
/// <c>destination</c>, versioned there. They go down level by level like the rest of what is inherited: a workspace in
/// between keeps them in its <c>_fx/.inherited/workspace/</c> (the merged entries in <c>workspace.yaml</c> and the
/// files, under their destination) to pass them on, and a repository they apply to receives them. A received file is
/// fx's because its <c>inherited.yaml</c> lists it (an image cannot take a header): fx never overwrites a file of the
/// repository's own (unless <c>--adopt</c>), and removes what it no longer gives. At the same destination, the nearest
/// wins. A folder (<c>path</c> ending in '/') shares its content.
/// </summary>
public static class WorkspaceShares
{
	public const string Missing = "workspace.share-missing";
	public const string NotVersioned = "workspace.share-not-versioned";
	public const string Refused = "workspace.share-refused";

	/// <summary>Where a workspace in between keeps the shares it passes on, inside <c>_fx/.inherited/</c>.</summary>
	public const string Folder = WorkspaceManifest.ModuleName;

	/// <summary>
	/// The shares <paramref name="parent"/> passes down: what it inherited (its <c>_fx/.inherited/workspace/</c>) and then
	/// its own, the nearest last; an own share replaces an inherited one with the same destination.
	/// <paramref name="fromOf"/>: where a file of its <c>_fx/.inherited/</c> comes from.
	/// </summary>
	public static IReadOnlyList<ShareFlow> Of(FxNode parent, FxIdentity parentIdentity, Func<string, IReadOnlyList<string>> fromOf)
	{
		var flows = new List<ShareFlow>();
		var stored = parent.Full($"{FxLayout.Inherited}/{Folder}");
		var storedYaml = Path.Combine(stored, $"{Folder}.yaml");
		if (FxFiles.Exists(storedYaml))
			foreach (var share in WorkspaceManifest.Read(parent.Root, FxFiles.ReadAllText(storedYaml)).Shares ?? [])
				if (SafeDestination(share.Destination) && share with { Path = share.Destination } is var rebased)
					flows.Add(new(rebased, Expand(rebased, Path.Combine(stored, rebased.Path), relative => fromOf($"{Folder}/{relative}").LastOrDefault() ?? parentIdentity.FullName)));
		var ownYaml = parent.Full(WorkspaceManifest.RelativePath);
		if (parent.IsWorkspace && FxFiles.Exists(ownYaml))
			foreach (var share in WorkspaceManifest.Read(parent.Root, FxFiles.ReadAllText(ownYaml)).Shares ?? [])
			{
				if (!SafeSource(share.Path) || !SafeDestination(share.Destination))
					continue; // the manifest of the parent reports it
				var absolute = share with { AppliesTo = share.AppliesTo.Absolute(parentIdentity.FullName) };
				flows.RemoveAll(f => string.Equals(f.Share.Destination, share.Destination, StringComparison.OrdinalIgnoreCase));
				flows.Add(new(absolute, Expand(absolute, parent.Full(share.Path), _ => parentIdentity.FullName)));
			}
		return flows;
	}

	/// <summary>What <paramref name="identity"/> receives: the files of the shares for it, by destination (the nearest wins).</summary>
	public static ReceivedShares For(IReadOnlyList<ShareFlow> flows, FxIdentity identity)
	{
		var files = new SortedDictionary<string, SharedFile>(StringComparer.OrdinalIgnoreCase);
		var kept = new List<string>();
		foreach (var flow in flows.Where(f => f.Share.AppliesTo.Matches(identity)))
			if (flow.Files is null)
				kept.Add(flow.Share.Destination);
			else
				foreach (var file in flow.Files)
					files[file.Destination] = file;
		return new(files, kept);
	}

	/// <summary>The source of the file a repository receives at <paramref name="destination"/> from <paramref name="share"/>, relative to the workspace that declares it; null if the share does not give it.</summary>
	public static string? Source(WorkspaceShare share, string destination)
	{
		if (!share.Path.EndsWith('/'))
			return string.Equals(destination, share.Destination, StringComparison.OrdinalIgnoreCase) ? share.Path : null;
		return destination.StartsWith(share.Destination, StringComparison.OrdinalIgnoreCase) ? share.Path + destination[share.Destination.Length..] : null;
	}

	/// <summary>Whether <paramref name="destination"/> is <paramref name="share"/> or inside it (a folder).</summary>
	public static bool Under(string destination, string share)
		=> share.EndsWith('/') ? destination.StartsWith(share, StringComparison.OrdinalIgnoreCase) : string.Equals(destination, share, StringComparison.OrdinalIgnoreCase);

	/// <summary><c>_fx/.inherited/workspace/workspace.yaml</c> of a workspace in between: the shares, each under its destination there.</summary>
	public static string ToYaml(IReadOnlyList<ShareFlow> flows, string from)
	{
		var shares = flows.Select(f =>
		{
			var entries = new List<(string, YamlValue)> { ("path", f.Share.Destination), ("destination", f.Share.Destination) };
			if (f.Share.AppliesTo.ToYaml() is { } appliesTo)
				entries.Add(("applies-to", appliesTo));
			return (YamlValue)new YamlValue.Map(entries.ToArray());
		}).ToArray();
		return FxHeader.Hash(from) + YamlWriter.Document(new(("schema", FxYamlReader.Schema), ("shares", new YamlValue.Block(shares))));
	}

	/// <summary>
	/// Writes in <paramref name="child"/> the files it receives and removes those fx gave it and no longer does; a file
	/// of its own at a destination is not overwritten (unless <c>--adopt</c>). Returns the files that are fx's now, for
	/// its <c>inherited.yaml</c>.
	/// </summary>
	public static IReadOnlyList<FxInheritedShare> Apply(FxNode child, ReceivedShares received, FxInherited? existing, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var owned = (existing?.Shares ?? []).GroupBy(s => s.Destination, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		var result = new List<FxInheritedShare>();
		foreach (var (destination, file) in received.Files)
		{
			var path = child.Full(destination);
			if (!owned.ContainsKey(destination) && FxFiles.Exists(path) && !FxOutputs.Same(path, file.Content) && !outputs.Adopt)
			{
				outputs.Refused(path);
				diagnostics.Add(FxDiagnostic.Error(Refused, new(Refused, outputs.Display(path), file.From), outputs.Display(path),
					fix: new FxFix("fx sync --adopt", new($"{Refused}.fix"))));
				continue;
			}
			outputs.Owned(path, file.Content);
			result.Add(new(destination, file.From));
		}
		// a share whose source is not there: what the repository has of it stays as it is
		foreach (var share in owned.Values.Where(s => !received.Files.ContainsKey(s.Destination) && received.Kept.Any(k => Under(s.Destination, k))))
			result.Add(share);
		// what it no longer gives
		foreach (var share in owned.Values.Where(s => !result.Any(r => string.Equals(r.Destination, s.Destination, StringComparison.OrdinalIgnoreCase))))
		{
			var path = child.Full(share.Destination);
			outputs.Delete(path);
			if (outputs.Write)
				for (var dir = Path.GetDirectoryName(path); dir is not null && !FxLayout.SameFolder(dir, child.Root) && FxFiles.DirectoryExists(dir)
				                                            && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
					Directory.Delete(dir);
		}
		return result.OrderBy(s => s.Destination, StringComparer.Ordinal).ToList();
	}

	/// <summary>
	/// In the doctor of the workspace that declares them: a share whose source is not there, or not versioned (first it
	/// is versioned, then shared: so a clone of the workspace has it).
	/// </summary>
	public static void Check(FxNode node, WorkspaceManifest manifest, string manifestFile, bool doctor, FxDiagnostics diagnostics)
	{
		foreach (var share in manifest.Shares ?? [])
		{
			var source = node.Full(share.Path);
			if (share.Path.EndsWith('/') ? !FxFiles.DirectoryExists(source) : !FxFiles.Exists(source))
				diagnostics.Add(FxDiagnostic.Warning(Missing, new(Missing, share.Path), manifestFile, share.Line));
			else if (doctor && !Versioned(manifest, share.Path))
				diagnostics.Add(FxDiagnostic.Error(NotVersioned, new(NotVersioned, share.Path), manifestFile, share.Line, fix: FxFix.Run($"fx path add {share.Path}")));
		}
	}

	/// <summary>Versioned by the workspace: in its <c>paths</c>, or in its <c>_fx/</c> (always versioned, but for what fx writes there).</summary>
	static bool Versioned(WorkspaceManifest manifest, string path)
		=> manifest.Versions(path)
		   || (path.StartsWith(FxLayout.Folder + "/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(FxLayout.Inherited + "/", StringComparison.OrdinalIgnoreCase)
		       && !path.Split('/').Any(FxLayout.IsLocal));

	/// <summary>Where a share can come from: relative, inside the root, out of <c>.git/</c>, <c>_fx/.inherited/</c> and the local files.</summary>
	public static bool SafeSource(string path)
		=> Relative(path, out var segments)
		   && !(string.Equals(segments[0], FxLayout.Folder, StringComparison.OrdinalIgnoreCase) && segments.Length > 1
		        && string.Equals(segments[1], ".inherited", StringComparison.OrdinalIgnoreCase));

	/// <summary>Where a share can go: relative, inside the repository, out of <c>.git/</c>, <c>_fx/</c> (its modules') and the local files.</summary>
	public static bool SafeDestination(string path)
		=> Relative(path, out var segments) && !string.Equals(segments[0], FxLayout.Folder, StringComparison.OrdinalIgnoreCase);

	static bool Relative(string path, out string[] segments)
	{
		segments = path.TrimEnd('/').Split('/');
		return path.Length > 0 && !Path.IsPathRooted(path)
		                       && !segments.Any(s => s is "" or "." or ".." || FxLayout.IsLocal(s))
		                       && !string.Equals(segments[0], ".git", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The files of a share: its file, or every file of its folder (but fx's local ones); none if its source is not there.</summary>
	static IReadOnlyList<SharedFile>? Expand(WorkspaceShare share, string source, Func<string, string> from)
	{
		if (!share.Path.EndsWith('/'))
			return FxFiles.Exists(source) ? [new(share.Destination, FxFiles.ReadAllBytes(source), from(share.Destination))] : null;
		if (!FxFiles.DirectoryExists(source))
			return null;
		return FxFiles.EnumerateFiles(source, "*", SearchOption.AllDirectories)
			.Select(f => FxLayout.Relative(source, f))
			.Where(r => !r.Split('/').Any(s => FxLayout.IsLocal(s) || s == ".git"))
			.Order(StringComparer.Ordinal)
			.Select(r => new SharedFile(share.Destination + r, FxFiles.ReadAllBytes(Path.Combine(source, r)), from(share.Destination + r)))
			.ToList();
	}
}
