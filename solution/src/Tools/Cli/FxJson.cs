using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Cli;

// The JSON of fx (plan O, decision 13): a document per command, with a versioned schema, in English, generated at
// compile time (AOT). Every document is: schema, ok, the command's data, and all the diagnostics with their scope.

public sealed record JsonFix(string? Command, string? Hint);

public sealed record JsonDiagnostic(string Code, string Severity, string Module, string? Repository, string Message, string? File, int? Line, JsonFix? Fix)
{
	public static JsonDiagnostic From(FxDiagnostic d) => new(
		d.Code,
		SeverityName(d.Severity),
		d.Module,
		d.Repository,
		d.Message.English,
		d.File,
		d.Line,
		d.Fix is null ? null : new(d.Fix.Command, d.Fix.Hint?.English));

	public static IReadOnlyList<JsonDiagnostic> From(IEnumerable<FxDiagnostic> diagnostics) => diagnostics.Select(From).ToList();

	public static string SeverityName(FxSeverity severity) => severity switch
	{
		FxSeverity.Error => "error",
		FxSeverity.Warning => "warning",
		_ => "info"
	};
}

public sealed record JsonFile(string Path, string Status);

public sealed record JsonRepositoryAction(string Name, string Action, string? Detail);

public sealed record JsonModule(string Module, string Root, string? Repository, IReadOnlyList<JsonFile> Files, IReadOnlyList<JsonRepositoryAction> Repositories)
{
	public static JsonModule From(FxModuleResult m) => new(
		m.Module,
		m.Root,
		m.Repository,
		m.Files.Select(f => new JsonFile(f.Path, FileStatus(f.Status))).ToList(),
		m.Actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList());

	public static string FileStatus(SyncFileStatus status) => status switch
	{
		SyncFileStatus.Written => "written",
		SyncFileStatus.WouldWrite => "would-write",
		SyncFileStatus.Refused => "refused",
		_ => "unchanged"
	};
}

/// <summary><c>fx-sync/1</c> and <c>fx-doctor/1</c>.</summary>
public sealed record SyncDocument(string Schema, bool Ok, IReadOnlyList<JsonModule> Modules, IReadOnlyList<JsonDiagnostic> Diagnostics);

public sealed record JsonRepository(
	string Name,
	string Path,
	string Mount,
	bool Mounted,
	bool Present,
	string? Branch = null,
	bool? Detached = null,
	string? Upstream = null,
	int? Ahead = null,
	int? Behind = null,
	int? Changes = null);

/// <summary><c>fx-repo-list/1</c> and <c>fx-repo-status/1</c>.</summary>
public sealed record RepositoriesDocument(string Schema, bool Ok, IReadOnlyList<JsonRepository> Repositories, IReadOnlyList<JsonDiagnostic> Diagnostics);

/// <summary><c>fx-repo-mount/1</c> (also for unmount).</summary>
public sealed record MountDocument(string Schema, bool Ok, IReadOnlyList<JsonDiagnostic> Diagnostics);

/// <summary><c>fx-repo-pull/1</c>.</summary>
public sealed record PullDocument(string Schema, bool Ok, IReadOnlyList<JsonRepositoryAction> Repositories, IReadOnlyList<JsonDiagnostic> Diagnostics);

public sealed record JsonTag(string Name, string Commit);

public sealed record JsonGit(
	string Repository,
	string Branch,
	string Rule,
	string Head,
	string StableTip,
	string? MergeBase,
	JsonTag Tag,
	int CommitsSinceTag,
	int? BranchCommits);

/// <summary><c>fx-version/1</c>.</summary>
public sealed record VersionDocument(
	string Schema,
	bool Ok,
	string? Version,
	string? PackageVersion,
	string? AssemblyVersion,
	string? FileVersion,
	string? InformationalVersion,
	JsonGit? Git,
	IReadOnlyList<JsonDiagnostic> Diagnostics);

/// <summary><c>fx-version-tag/1</c>.</summary>
public sealed record VersionTagDocument(string Schema, bool Ok, string? Tag, string? Commit, IReadOnlyList<JsonDiagnostic> Diagnostics);

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = true)]
[JsonSerializable(typeof(SyncDocument))]
[JsonSerializable(typeof(RepositoriesDocument))]
[JsonSerializable(typeof(MountDocument))]
[JsonSerializable(typeof(PullDocument))]
[JsonSerializable(typeof(VersionDocument))]
[JsonSerializable(typeof(VersionTagDocument))]
public sealed partial class FxJsonContext : JsonSerializerContext;
