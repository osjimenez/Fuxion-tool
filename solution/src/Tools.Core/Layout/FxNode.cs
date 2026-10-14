using System;
using System.Collections.Generic;
using System.IO;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Layout;

/// <summary>
/// A root of fx on disk: a repository or a workspace (plan P, decision 1). It knows itself by its
/// <c>_fx/.inherited/inherited.yaml</c> (what its workspace said last time) or, if nobody declares it, by the
/// <c>name</c> of its <c>_fx/fx.yaml</c>.
/// </summary>
public sealed class FxNode(string root)
{
	public const string NameMissing = "fx.name-missing";
	public const string NotARoot = "fx.not-a-root";

	public string Root { get; } = FxLayout.Normalize(root);

	/// <summary>The root of fx that contains <paramref name="directory"/>, if any (<see cref="FxLayout.FindRoot"/>).</summary>
	public static FxNode? Find(string directory) => FxLayout.FindRoot(directory) is { } root ? new(root) : null;

	public bool Exists => FxLayout.IsGitRoot(Root) || FxFiles.DirectoryExists(FxLayout.Full(Root, FxLayout.Folder));

	public bool IsWorkspace => WorkspaceManifest.IsWorkspace(Root);

	/// <summary>The name of its folder (what a root without a name falls back to).</summary>
	public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Root));

	/// <summary>Whether it has the folder of <paramref name="module"/> in its <c>_fx/</c>.</summary>
	public bool HasModule(string module) => FxFiles.DirectoryExists(FxLayout.Full(Root, FxLayout.ModuleFolder(module)));

	public string Full(string relative) => FxLayout.Full(Root, relative);

	public FxInherited? Inherited(ICollection<FxDiagnostic>? diagnostics = null) => FxInherited.Load(Root, diagnostics);

	/// <summary>Its own <c>_fx/fx.yaml</c>.</summary>
	public FxConfig OwnConfig(ICollection<FxDiagnostic>? diagnostics = null) => FxConfig.Load(Root, FxLayout.ConfigFile, diagnostics ?? []);

	/// <summary>What it inherits (<c>_fx/.inherited/fx.yaml</c>) with its own on top.</summary>
	public FxConfig Config(ICollection<FxDiagnostic>? diagnostics = null)
		=> FxConfig.Merge(FxConfig.Load(Root, $"{FxLayout.Inherited}/fx.yaml", diagnostics ?? []), OwnConfig(diagnostics), FxLayout.ConfigFile, diagnostics);

	/// <summary>
	/// Who it is when nobody on disk declares it: what its workspace said last time, or its own name as a root. A root
	/// without a name gets its folder's, and an error in <paramref name="diagnostics"/>: a root needs one (decision 4).
	/// </summary>
	public FxIdentity SelfIdentity(ICollection<FxDiagnostic>? diagnostics = null)
	{
		if (Inherited() is { } inherited)
			return inherited.Repository;
		if (OwnConfig().Name is { } name)
			return FxIdentity.Root(name);
		// a repository without _fx/ is not a root of fx: nothing to ask of it
		if (FxFiles.DirectoryExists(Full(FxLayout.Folder)))
			diagnostics?.Add(FxDiagnostic.Error(NameMissing, new(NameMissing, FolderName), FxLayout.ConfigFile, fix: FxFix.Do($"{NameMissing}.fix")));
		return FxIdentity.Root(FxConfig.NamePattern().IsMatch(FolderName) ? FolderName : "root");
	}

	/// <summary>
	/// The workspace on disk that declares it as its own (not as a guest), with that declaration: the nearest above
	/// that does. A guest declaration is skipped: its parent is further up. The root of a worktree is its main clone in
	/// worktree mode: its parent is that one's, in the main tree (plan P, decision 28).
	/// </summary>
	public (FxNode Workspace, WorkspaceManifest Manifest, WorkspaceRepository Declaration)? FindParent()
	{
		if (FxFiles.Exists(Full(FxLayout.WorktreeFile)) && Git.GitClient.Discover(Root)?.MainRoot() is { } main && !FxLayout.SameFolder(main, Root))
			return new FxNode(main).FindParent();
		for (var dir = Directory.GetParent(Path.TrimEndingDirectorySeparator(Root)); dir is not null; dir = dir.Parent)
		{
			if (!WorkspaceManifest.IsWorkspace(dir.FullName))
				continue;
			var file = FxLayout.Full(dir.FullName, WorkspaceManifest.RelativePath);
			var manifest = WorkspaceManifest.Read(FxLayout.Normalize(dir.FullName), FxFiles.Exists(file) ? FxFiles.ReadAllText(file) : "");
			if (manifest.RepositoryAt(Root) is { Inherits: true } declaration)
				return (new FxNode(dir.FullName), manifest, declaration);
		}
		return null;
	}

	public override string ToString() => Root;
}
