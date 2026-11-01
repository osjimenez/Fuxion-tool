using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Layout;

/// <summary>
/// A root of fx on disk: a repository or a space. It knows itself by its
/// <c>_fx/.inherited/inherited.yaml</c> (what its space said last time) or, if nobody declares it, by the
/// <c>name</c> of its <c>_fx/space.yaml</c>.
/// </summary>
public sealed class FxNode(string root)
{
	public const string NameMissing = "space.name-missing";
	public const string NotARoot = "space.not-a-space";

	public string Root { get; } = FxLayout.Normalize(root);

	/// <summary>The root of fx that contains <paramref name="directory"/>, if any (<see cref="FxLayout.FindRoot"/>).</summary>
	public static FxNode? Find(string directory) => FxLayout.FindRoot(directory) is { } root ? new(root) : null;

	public bool Exists => FxLayout.IsGitRoot(Root) || FxFiles.DirectoryExists(FxLayout.Full(Root, FxLayout.Folder));

	public bool HasMounts => MountsManifest.HasMounts(Root);

	/// <summary>The name of its folder (what a root without a name falls back to).</summary>
	public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Root));

	/// <summary>Whether it has the folder of <paramref name="module"/> in its <c>_fx/</c>.</summary>
	public bool HasModule(string module) => FxFiles.DirectoryExists(FxLayout.Full(Root, FxLayout.ModuleFolder(module)));

	public string Full(string relative) => FxLayout.Full(Root, relative);

	/// <summary>What its space said last time (<c>_fx/.inherited/inherited.yaml</c>), if it is there and can be read.</summary>
	public FxInherited? Inherited() => FxInherited.Load(Root);

	/// <summary>Whether it declares itself a space: it has its own <c>_fx/space.yaml</c> (a repository only inherits one).</summary>
	public bool IsSpace => FxFiles.Exists(Full(FxLayout.ConfigFile));

	/// <summary>What can be read of its own <c>_fx/space.yaml</c>, whatever its problems.</summary>
	public SpaceConfig OwnConfig() => SpaceConfig.Load(Root);

	/// <summary>What it inherits (<c>_fx/.inherited/space.yaml</c>) with its own on top, whatever their problems.</summary>
	public SpaceConfig Config() => SpaceConfig.Combine(SpaceConfig.Load(Root, FxLayout.InheritedConfigFile), OwnConfig());

	/// <summary>What it inherits with its own on top, read with their problems: an error if they have one.</summary>
	public Response<SpaceConfig> ReadConfig()
	{
		var inherited = SpaceConfig.ReadFile(Root, FxLayout.InheritedConfigFile);
		var own = SpaceConfig.ReadFile(Root);
		var merged = SpaceConfig.Merge(inherited is SpaceConfig i ? i : SpaceConfig.Load(Root, FxLayout.InheritedConfigFile), own is SpaceConfig o ? o : OwnConfig(),
			FxLayout.ConfigFile);
		var found = new List<FxDiagnostic>([.. inherited.Diagnostics, .. own.Diagnostics, .. merged.Diagnostics]);
		return FxResponse.Outcome(merged is SpaceConfig config ? config : Config(), found, HttpStatusCode.Conflict);
	}

	/// <summary>
	/// Who it is when nobody on disk declares it: what its space said last time, or its own name as a root. A root
	/// without a name gets its folder's (<see cref="ReadSelfIdentity"/> says it is an error: a root needs one).
	/// </summary>
	public FxIdentity SelfIdentity() => ReadSelfIdentity() is FxIdentity identity ? identity : FolderIdentity;

	/// <summary>Who it is when nobody on disk declares it (<see cref="SelfIdentity"/>); an error if it is a root without a name.</summary>
	public Response<FxIdentity> ReadSelfIdentity()
	{
		if (Inherited() is { } inherited)
			return inherited.Space;
		if (OwnConfig().Name is { } name)
			return FxIdentity.Root(name);
		// a repository without _fx/ is not a root of fx: nothing to ask of it
		if (FxFiles.DirectoryExists(Full(FxLayout.Folder)))
			return FxErrors.Of(HttpStatusCode.Conflict, FxDiagnostic.Error(NameMissing, new(NameMissing, FolderName), FxLayout.ConfigFile, fix: FxFix.Do($"{NameMissing}.fix")));
		return FolderIdentity;
	}

	/// <summary>Its folder's name, as a root (or <c>root</c>, if the folder's is not a name).</summary>
	FxIdentity FolderIdentity => FxIdentity.Root(SpaceConfig.NamePattern().IsMatch(FolderName) ? FolderName : "root");


	/// <summary>
	/// The space on disk that declares it as its own (not as a guest), with that declaration: the nearest above
	/// that does. A guest declaration is skipped: its parent is further up. The root of a worktree is its main clone in
	/// worktree mode: its parent is that one's, in the main tree.
	/// </summary>
	public (FxNode Space, MountsManifest Manifest, Mount Declaration)? FindParent()
	{
		if (FxFiles.Exists(Full(FxLayout.WorktreeFile)) && Git.GitClient.Discover(Root)?.MainRoot() is { } main && !FxLayout.SameFolder(main, Root))
			return new FxNode(main).FindParent();
		for (var dir = Directory.GetParent(Path.TrimEndingDirectorySeparator(Root)); dir is not null; dir = dir.Parent)
		{
			if (!MountsManifest.HasMounts(dir.FullName))
				continue;
			var file = FxLayout.Full(dir.FullName, MountsManifest.RelativePath);
			var manifest = MountsManifest.Parse(FxLayout.Normalize(dir.FullName), FxFiles.Exists(file) ? FxFiles.ReadAllText(file) : "");
			if (manifest.MountAt(Root) is { Inherits: true } declaration)
				return (new FxNode(dir.FullName), manifest, declaration);
		}
		return null;
	}

	public override string ToString() => Root;
}
