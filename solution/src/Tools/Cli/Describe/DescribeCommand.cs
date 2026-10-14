using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Completions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Fuxion.Tools.Cli.Repo;
using Fuxion.Tools.Cli.Sync;
using Fuxion.Tools.Cli.Version;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace Fuxion.Tools.Cli.Describe;

/// <summary>
/// <c>fx describe</c> and <c>fx schema</c> (plan O, decision 12): fx described by itself, for agents, the skill of fx,
/// the documentation of the CLI and, later, MCP. <c>fx describe</c> gives every command with its arguments, its options,
/// the schema of its JSON, whether it changes something, uses the network or takes <c>--dry-run</c>, and the exit codes;
/// <c>fx schema &lt;id&gt;</c>, the JSON Schema of an output, made from the types that write it. Always in English, as the
/// JSON of fx.
/// </summary>
public static class DescribeCommand
{
	public const string Schema = "fx-describe/1";
	public const string SchemasSchema = "fx-schemas/1";
	public const string EventsSchema = "fx-events/1";
	const string UnknownSchema = "fx.unknown-schema";

	/// <summary>The exit codes of fx (plan O, O3.1): the same for every command; what each means, in <c>describe.exit-code.&lt;code&gt;</c>.</summary>
	public static readonly IReadOnlyList<int> ExitCodes = [0, 1, FxConsole.UsageError, FxConsole.Cancelled];

	public static IEnumerable<Command> Create(GlobalOptions global, string currentDirectory)
	{
		var describe = new Command("describe", Texts.Get("describe")).Describe(Schema);
		describe.SetAction(result =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return Describe(console, currentDirectory);
		});
		yield return describe;

		var id = new Argument<string?>("id") { Description = Texts.Get("schema.id"), Arity = ArgumentArity.ZeroOrOne };
		var schema = new Command("schema", Texts.Get("schema")) { id }.Describe(SchemasSchema);
		schema.SetAction(result =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return SchemaOf(console, currentDirectory, result.GetValue(id));
		});
		yield return schema;
	}

	/// <summary>The commands of fx, in English (the language of its JSON), with what they do: a tree built for it.</summary>
	static IReadOnlyList<(string Name, Command Command)> Commands(string currentDirectory, out RootCommand root)
	{
		root = English(() => FxApp.Build(currentDirectory));
		var result = new List<(string, Command)>();
		void Walk(Command command, string path)
		{
			foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
			{
				var name = path.Length == 0 ? sub.Name : $"{path} {sub.Name}";
				if (sub.Action is not null)
					result.Add((name, sub));
				Walk(sub, name);
			}
		}
		Walk(root, "");
		return result;
	}

	/// <summary>What <paramref name="make"/> makes, with the texts in English (the culture of this thread, for a moment).</summary>
	static T English<T>(Func<T> make)
	{
		var culture = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
		try
		{
			return make();
		}
		finally
		{
			CultureInfo.CurrentUICulture = culture;
		}
	}

	static JsonOption Option(Option option)
	{
		var type = option.ValueType == typeof(bool) || option.ValueType == typeof(void) ? "bool"
			: option.ValueType == typeof(string) ? "string"
			: option.ValueType == typeof(string[]) ? "string[]"
			: option.ValueType.Name.ToLowerInvariant();
		var values = option.ValueType == typeof(bool) || option.ValueType == typeof(void) ? null : option.GetCompletions(CompletionContext.Empty).Select(c => c.Label).ToList();
		return new(option.Name, option.Aliases.Order(StringComparer.Ordinal).ToList(), option.Description ?? "", type, values is { Count: > 0 } ? values : null, option.Required);
	}

	static string Arity(ArgumentArity arity)
		=> arity.MinimumNumberOfValues == arity.MaximumNumberOfValues ? $"{arity.MinimumNumberOfValues}"
			: $"{arity.MinimumNumberOfValues}..{(arity.MaximumNumberOfValues == int.MaxValue ? "*" : arity.MaximumNumberOfValues.ToString(CultureInfo.InvariantCulture))}";

	static int Describe(FxConsole console, string currentDirectory)
	{
		var commands = Commands(currentDirectory, out var root);
		if (console.Settings.IsMachine)
		{
			var global = root.Options.Where(o => o.Recursive || o.Name is "--help").Select(Option).ToList();
			console.WriteJson(new DescribeDocument(Schema, true, SyncCommand.Version ?? "", commands.Select(c =>
			{
				var info = c.Command.Info();
				return new JsonCommand(c.Name, c.Command.Description ?? "",
					c.Command.Arguments.Select(a => new JsonArgument(a.Name, a.Description ?? "", Arity(a.Arity))).ToList(),
					c.Command.Options.Select(Option).ToList(),
					info?.Schema, info?.Writes ?? false, info?.Network ?? false, c.Command.HasDryRun());
			}).ToList(), global, ExitCodes.Select(code => new JsonExitCode(code, English(() => Texts.Get($"describe.exit-code.{code}")))).ToList(), []), c => c.DescribeDocument);
			return 0;
		}
		// for people, what an agent needs at a glance: what each one outputs and does (fx --help has the rest)
		var table = Table(console, Texts.Get("describe.column.command"), Texts.Get("describe.column.output"), Texts.Get("describe.column.writes"),
			Texts.Get("describe.column.network"), "--dry-run");
		foreach (var (name, command) in commands)
		{
			var info = command.Info();
			table.AddRow(new TextBlock($"fx {name}"), new TextBlock(info?.Schema ?? ""), new TextBlock(Mark(info?.Writes ?? false)),
				new TextBlock(Mark(info?.Network ?? false)), new TextBlock(Mark(command.HasDryRun())));
		}
		console.Write(table);
		console.Line();
		console.Line($"[bold]{FxConsole.Escape(Texts.Get("describe.exit-codes"))}[/]");
		foreach (var code in ExitCodes)
			console.Line($"  {code,-4} {FxConsole.Escape(Texts.Get($"describe.exit-code.{code}"))}");
		console.Line();
		console.Line($"[gray]{FxConsole.Escape(Texts.Get("describe.more"))}[/]");
		return 0;

		string Mark(bool yes) => yes ? Texts.Get("yes") : "";
	}

	static Table Table(FxConsole console, params string[] headers)
	{
		var table = new Table().Headers(headers.Select(h => (Visual)new TextBlock(h)).ToArray());
		if (!console.Capabilities.CanColor)
			table.Style(TableStyle.Default with { ShowOuterBorder = false, ShowVerticalLines = false, ShowHeaderSeparator = false, CellPadding = new(0, 0, 2, 0) });
		return table;
	}

	/// <summary>Every schema of fx and the commands that write it (the events, every one with <c>--output ndjson</c>).</summary>
	static IReadOnlyList<(string Id, IReadOnlyList<string> Commands)> Schemas(IReadOnlyList<(string Name, Command Command)> commands)
		=> commands.Where(c => c.Command.Info()?.Schema is not null)
			.GroupBy(c => c.Command.Info()!.Schema!)
			.Select(g => (g.Key, (IReadOnlyList<string>)g.Select(c => $"fx {c.Name}").ToList()))
			.Append((EventsSchema, ["fx <command> --output ndjson"]))
			.OrderBy(s => s.Item1, StringComparer.Ordinal)
			.ToList();

	/// <summary>The type that writes the document of a schema.</summary>
	static JsonTypeInfo? TypeOf(string id)
		=> id switch
		{
			SyncCommand.SyncSchema or SyncCommand.DoctorSchema => FxJsonContext.Default.SyncDocument,
			"fx-repo-list/1" or "fx-repo-status/1" => FxJsonContext.Default.RepositoriesDocument,
			"fx-repo-mount/1" => FxJsonContext.Default.MountDocument,
			"fx-repo-pull/1" => FxJsonContext.Default.PullDocument,
			ManifestCommands.Schema => FxJsonContext.Default.ManifestEditDocument,
			VersionCommand.Schema => FxJsonContext.Default.VersionDocument,
			VersionTagCommand.Schema => FxJsonContext.Default.VersionTagDocument,
			"fx-worktree-list/1" => FxJsonContext.Default.WorktreeListDocument,
			"fx-worktree-add/1" or "fx-worktree-mount/1" or "fx-worktree-unmount/1" or "fx-worktree-remove/1" => FxJsonContext.Default.WorktreeDocument,
			Schema => FxJsonContext.Default.DescribeDocument,
			SchemasSchema => FxJsonContext.Default.SchemasDocument,
			_ => null
		};

	/// <summary>The JSON Schema (2020-12) of the document <paramref name="id"/>, from its type; null if fx has none with that id.</summary>
	public static JsonObject? JsonSchema(string id)
	{
		var options = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true };
		JsonObject schema;
		if (id == EventsSchema)
			schema = new JsonObject
			{
				["description"] = "One event per line as the work goes on; the last line is the document of the command, with its own schema.",
				["oneOf"] = new JsonArray(
					FxJsonContext.Default.JsonStartEvent.GetJsonSchemaAsNode(options),
					FxJsonContext.Default.JsonStepEvent.GetJsonSchemaAsNode(options),
					FxJsonContext.Default.JsonDiagnosticEvent.GetJsonSchemaAsNode(options))
			};
		else if (TypeOf(id) is { } type)
			schema = type.GetJsonSchemaAsNode(options).AsObject();
		else
			return null;
		var result = new JsonObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$id"] = id };
		foreach (var (key, value) in schema.ToList())
		{
			schema.Remove(key);
			result[key] = value;
		}
		// the schema property of a document is its id
		if (result["properties"]?["schema"] is JsonObject property)
			property["const"] = id;
		Optional(result);
		return result;
	}

	/// <summary>
	/// A property that can be null is not required: fx leaves the nulls out of its JSON (<c>WhenWritingNull</c>), and the
	/// exporter takes every parameter of a record for required.
	/// </summary>
	static void Optional(JsonNode? node)
	{
		switch (node)
		{
			case JsonObject map:
				if (map["properties"] is JsonObject properties && map["required"] is JsonArray required)
				{
					foreach (var name in required.Select(r => (string?)r).OfType<string>().ToList())
						if (properties[name]?["type"] is JsonArray types && types.Any(t => (string?)t == "null"))
							required.Remove(required.First(r => (string?)r == name));
					if (required.Count == 0)
						map.Remove("required");
				}
				foreach (var (_, value) in map.ToList())
					Optional(value);
				break;
			case JsonArray list:
				foreach (var item in list.ToList())
					Optional(item);
				break;
		}
	}

	static int SchemaOf(FxConsole console, string currentDirectory, string? id)
	{
		var schemas = Schemas(Commands(currentDirectory, out _));
		if (id is null)
		{
			if (console.Settings.IsMachine)
			{
				console.WriteJson(new SchemasDocument(SchemasSchema, true, schemas.Select(s => new JsonSchemaInfo(s.Id, s.Commands)).ToList(), []), c => c.SchemasDocument);
				return 0;
			}
			var table = Table(console, Texts.Get("schema.column.id"), Texts.Get("schema.column.commands"));
			foreach (var (schemaId, commands) in schemas)
				table.AddRow(new TextBlock(schemaId), new TextBlock(string.Join(", ", commands)) { Wrap = true });
			console.Write(table);
			console.Line();
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("schema.more"))}[/]");
			return 0;
		}
		if (JsonSchema(id) is not { } schema)
		{
			console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get(UnknownSchema, id))}[/]");
			return FxConsole.UsageError;
		}
		// the schema is the data: always JSON, whatever --output
		var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
			schema.WriteTo(writer);
		console.Raw(Encoding.UTF8.GetString(buffer.ToArray()));
		return 0;
	}
}
