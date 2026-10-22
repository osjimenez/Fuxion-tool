using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>An entry of a register, as a branch has it.</summary>
public sealed record DocBranchEntry(string Id, string Name, string? Key);

/// <summary>A document of the catalog as a branch has it: its branch, its path in the catalog, its identity, its type and its entries.</summary>
public sealed record DocBranchDocument(string Branch, string Path, string Id, string? Type, IReadOnlyList<DocBranchEntry> Entries);

/// <summary>
/// The catalog against the remote branches git knows (the SPEC of the module, 5.2, <c>--branches</c>): what another branch
/// creates must not collide with what is here, so that joining them gives a valid catalog. Without network (git has them
/// already; <c>--fetch</c> brings them first). Only what each branch has different from <c>HEAD</c> is read: what is the
/// same cannot collide.
/// </summary>
public static class DocBranches
{
	public const string BranchCollision = "docs.branch-collision";
	public const string FetchFailed = "docs.fetch-failed";

	/// <summary>Brings the remote branches (<c>--fetch</c>); a problem, if it could not.</summary>
	public static FxDiagnostic? Fetch(DocCatalog catalog)
	{
		if (GitClient.Discover(catalog.Root) is not { } git)
			return null;
		var fetch = git.Fetch(GitClient.DefaultTimeout);
		return fetch.ExitCode == 0 ? null : FxDiagnostic.Warning(FetchFailed, new(FetchFailed, fetch.StandardError.Trim()));
	}

	/// <summary>The documents of the catalog that the remote branches git knows have different from <c>HEAD</c>.</summary>
	public static IReadOnlyList<DocBranchDocument> Read(DocCatalog catalog)
	{
		if (GitClient.Discover(catalog.Root) is not { } git)
			return [];
		var folder = Path.GetRelativePath(git.Root, catalog.Folder).Replace('\\', '/');
		var head = git.Head();
		var documents = new List<DocBranchDocument>();
		var refs = git.Execute("for-each-ref", "--format=%(refname)", "refs/remotes").StandardOutput
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(r => !r.EndsWith("/HEAD", StringComparison.Ordinal));
		foreach (var reference in refs)
		{
			var changed = head is null
				? git.Execute("ls-tree", "-r", "--name-only", reference, "--", folder)
				: git.Execute("diff", "--name-only", "--no-renames", head, reference, "--", folder);
			foreach (var path in changed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				         .Where(p => p.EndsWith(DocSidecar.Extension, StringComparison.Ordinal)))
			{
				// removed in the branch: nothing to collide with
				if (git.Execute("show", $"{reference}:{path}") is not { ExitCode: 0 } show)
					continue;
				var (id, type, entries) = Read(show.StandardOutput);
				if (id is null)
					continue;
				var relative = path[(folder.Length + 1)..];
				documents.Add(new(reference["refs/remotes/".Length..], relative[..^DocSidecar.Extension.Length] + ".md", id, type, entries));
			}
		}
		return documents;
	}

	/// <summary>The identity, the type and the entries of a sidecar, read loosely; no identity if it cannot be read.</summary>
	static (string? Id, string? Type, IReadOnlyList<DocBranchEntry> Entries) Read(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return (null, null, []);
			string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
			var entries = root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array
				? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && Text(e, "id") is not null && Text(e, "name") is not null)
					.Select(e => new DocBranchEntry(Text(e, "id")!, Text(e, "name")!, Text(e, "key")))
					.ToList()
				: [];
			return (Text(root, "id"), Text(root, "type"), entries);
		}
		catch (JsonException)
		{
			return (null, null, []);
		}
	}

	static string FolderOf(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

	static string StemOf(string path) => Path.GetFileNameWithoutExtension(path);

	/// <summary>The slug of a name of the standard (<c>TYPE-slug</c>); null if it is not one.</summary>
	static string? SlugOf(string stem) => stem.IndexOf('-') is var dash and > 0 && DocTypes.FromToken(stem[..dash]) is not null ? stem[(dash + 1)..] : null;

	/// <summary>
	/// The collisions of <paramref name="documents"/> (those of the catalog here) with <paramref name="branches"/>: the same
	/// path, another document; the same slug in the folder, another name; the same identity, another document (not a
	/// document a branch moved: then its old path is no longer in the branch); an entry of the same register with the name
	/// or the key of another.
	/// </summary>
	public static IEnumerable<FxDiagnostic> Collisions(DocCatalog catalog, IReadOnlyList<DocDocument> documents, IReadOnlyList<DocBranchDocument> branches,
		Func<string, string> display)
	{
		var git = GitClient.Discover(catalog.Root);
		var folder = git is null ? "" : Path.GetRelativePath(git.Root, catalog.Folder).Replace('\\', '/');
		bool InBranch(string branch, string path)
			=> git?.Execute("cat-file", "-e", $"refs/remotes/{branch}:{folder}/{path[..^".md".Length]}{DocSidecar.Extension}").ExitCode == 0;
		var byPath = documents.ToDictionary(d => d.File.Path, StringComparer.Ordinal);
		var byId = documents.GroupBy(d => d.Sidecar.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
		foreach (var other in branches)
		{
			if (byPath.TryGetValue(other.Path, out var same) && !string.Equals(same.Sidecar.Id, other.Id, StringComparison.OrdinalIgnoreCase))
				yield return Collision(same, "path", other.Path, other.Branch);
			if (SlugOf(StemOf(other.Path)) is { } slug)
				foreach (var here in catalog.Files.Where(f => f.Folder == FolderOf(other.Path) && f.Formal && f.Slug == slug && f.Stem != StemOf(other.Path)))
					if (documents.FirstOrDefault(d => d.File == here) is { } document)
						yield return Collision(document, "slug", other.Path, other.Branch);
			if (byId.TryGetValue(other.Id, out var twin) && twin.File.Path != other.Path && InBranch(other.Branch, twin.File.Path))
				yield return Collision(twin, "id", other.Path, other.Branch);
			if (same?.Type.Name != "register" || other.Type != "register")
				continue;
			foreach (var entry in other.Entries)
				foreach (var mine in same.Sidecar.Entries.Where(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
					if (mine.Name == entry.Name)
						yield return Collision(same, "entry", mine.Name, other.Branch);
					else if (mine.Key is not null && mine.Key == entry.Key)
						yield return Collision(same, "key", mine.Key, other.Branch);
		}

		FxDiagnostic Collision(DocDocument here, string what, string other, string branch)
			=> FxDiagnostic.Error(BranchCollision, new($"{BranchCollision}.{what}", here.File.Path, other, branch), display(here.File.Sidecar!));
	}

	/// <summary>The branch that has a document with the name <paramref name="stem"/>, or with <paramref name="slug"/> in <paramref name="folder"/>; null if none.</summary>
	public static string? Taken(DocCatalog catalog, string folder, string stem, string? slug)
		=> Read(catalog).FirstOrDefault(d => StemOf(d.Path) == stem || slug is not null && FolderOf(d.Path) == folder && SlugOf(StemOf(d.Path)) == slug) is { } taken
			? $"{taken.Branch} ({taken.Path})"
			: null;
}
