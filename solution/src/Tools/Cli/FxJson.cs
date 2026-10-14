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
	bool Parked,
	string? SupersededBy,
	string? Branch = null,
	bool? Detached = null,
	string? Upstream = null,
	int? Ahead = null,
	int? Behind = null,
	int? Changes = null);

/// <summary><c>fx-repo-list/1</c> and <c>fx-repo-status/1</c>.</summary>
public sealed record RepositoriesDocument(string Schema, bool Ok, IReadOnlyList<JsonRepository> Repositories, IReadOnlyList<JsonDiagnostic> Diagnostics);

/// <summary><c>fx-repo-mount/1</c> (also for unmount): what happened to each repository.</summary>
public sealed record MountDocument(string Schema, bool Ok, IReadOnlyList<JsonRepositoryAction> Repositories, IReadOnlyList<JsonDiagnostic> Diagnostics);

public sealed record JsonArgument(string Name, string Description, string Arity);

/// <param name="Values">The values it takes, if only some.</param>
public sealed record JsonOption(string Name, IReadOnlyList<string> Aliases, string Description, string Type, IReadOnlyList<string>? Values, bool Required);

/// <param name="Output">The schema of its JSON (<c>--output json</c>).</param>
/// <param name="Writes">Whether it changes something: files, clones, branches, tags.</param>
/// <param name="Network">Whether it may use the network.</param>
/// <param name="DryRun">Whether it takes <c>--dry-run</c>.</param>
/// <param name="Interactive">Whether it needs a terminal and a person (<c>fx ui</c>): no JSON, not for agents.</param>
public sealed record JsonCommand(string Name, string Description, IReadOnlyList<JsonArgument> Arguments, IReadOnlyList<JsonOption> Options, string? Output, bool Writes,
	bool Network, bool DryRun, bool Interactive);

public sealed record JsonExitCode(int Code, string Meaning);

/// <summary><c>fx-describe/1</c>: fx described by itself (plan O, decision 12).</summary>
public sealed record DescribeDocument(string Schema, bool Ok, string Version, IReadOnlyList<JsonCommand> Commands, IReadOnlyList<JsonOption> GlobalOptions,
	IReadOnlyList<JsonExitCode> ExitCodes, IReadOnlyList<JsonDiagnostic> Diagnostics);

public sealed record JsonSchemaInfo(string Id, IReadOnlyList<string> Commands);

/// <summary><c>fx-schemas/1</c>: the schemas of fx and the commands that write each one.</summary>
public sealed record SchemasDocument(string Schema, bool Ok, IReadOnlyList<JsonSchemaInfo> Schemas, IReadOnlyList<JsonDiagnostic> Diagnostics);

/// <summary><c>fx-worktree-add/1</c>, <c>-mount/1</c>, <c>-unmount/1</c> and <c>-remove/1</c>: the worktree, what happened to each repository and the sync that followed.</summary>
public sealed record WorktreeDocument(string Schema, bool Ok, string? Path, string Branch, IReadOnlyList<JsonRepositoryAction> Repositories, IReadOnlyList<JsonModule> Modules,
	IReadOnlyList<JsonDiagnostic> Diagnostics);

public sealed record JsonWorktreeMount(string Path, string Repository, bool CreatedBranch, int? Changes);

public sealed record JsonWorktree(string Branch, string Path, string From, IReadOnlyList<string> LeftOut, IReadOnlyList<JsonWorktreeMount> Repositories);

/// <summary><c>fx-worktree-list/1</c>.</summary>
public sealed record WorktreeListDocument(string Schema, bool Ok, IReadOnlyList<JsonWorktree> Worktrees, IReadOnlyList<JsonDiagnostic> Diagnostics);

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

/// <summary>A file a command that edits changes, and its diff.</summary>
public sealed record JsonFileEdit(string File, string Diff);

/// <summary>
/// <c>fx-manifest-edit/1</c>, of the commands that edit the manifest: whether it changed and was written (not with
/// <c>--dry-run</c> nor with errors) and the change as <c>diff -u</c> hunks. With <c>--dry-run</c>, what
/// <c>fx sync</c> would do with the change (<paramref name="Sync"/>: its modules, as in <c>fx-sync/1</c>).
/// </summary>
public sealed record ManifestEditDocument(string Schema, bool Ok, bool Changed, bool Written, IReadOnlyList<JsonFileEdit> Files, IReadOnlyList<JsonDiagnostic> Diagnostics,
	IReadOnlyList<JsonModule>? Sync = null);

// The events of --output ndjson (fx-events/1): one per line, "type" first; the last line is the command's document.

public sealed record JsonStartEvent(string Type, string Schema);

/// <summary><c>step-started</c> and <c>step-finished</c> (this one with whether it succeeded and how long it took).</summary>
public sealed record JsonStepEvent(string Type, string Module, string? Repository, string Step, bool? Succeeded, long? ElapsedMs);

public sealed record JsonDiagnosticEvent(string Type, JsonDiagnostic Diagnostic);

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
[JsonSerializable(typeof(ManifestEditDocument))]
[JsonSerializable(typeof(WorktreeDocument))]
[JsonSerializable(typeof(WorktreeListDocument))]
[JsonSerializable(typeof(DescribeDocument))]
[JsonSerializable(typeof(SchemasDocument))]
[JsonSerializable(typeof(JsonStartEvent))]
[JsonSerializable(typeof(JsonStepEvent))]
[JsonSerializable(typeof(JsonDiagnosticEvent))]
public sealed partial class FxJsonContext : JsonSerializerContext;
