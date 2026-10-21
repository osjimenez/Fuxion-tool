using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A relation, stated only in its source: its kind and the identity of its target.</summary>
public sealed record DocRelation(string Kind, string TargetId);

/// <summary>What a generated document comes from: its recipe and its scope, a path relative to the root.</summary>
public sealed record DocGeneration(string Recipe, string Scope);

/// <summary>The two hashes of a document: its content and its metadata.</summary>
public sealed record DocHashes(string Content, string Metadata);

/// <summary>
/// The sidecar of a document, <c>TIPO-slug.meta.json</c> (the standard, its metadata model): its identity, its type,
/// profile and status, its title, tags, dates and actor, its relations, its generation and its integrity. Read from a
/// valid one (<see cref="From"/>) and written in the order of the standard (<see cref="ToJson"/>).
/// </summary>
public sealed record DocSidecar
{
	public const string Extension = ".meta.json";

	public string SchemaVersion { get; init; } = DocsSchemas.Version;
	public required string Id { get; init; }
	public required string Type { get; init; }
	public string? Profile { get; init; }
	public required string Status { get; init; }
	public required string Title { get; init; }
	public IReadOnlyList<string> Tags { get; init; } = [];

	/// <summary>RFC 3339 in UTC, as it is written (<see cref="Instant"/>).</summary>
	public required string CreatedAt { get; init; }

	public required string UpdatedAt { get; init; }
	public required string CreatedBy { get; init; }
	public IReadOnlyList<DocRelation> Relations { get; init; } = [];
	public DocGeneration? Generation { get; init; }

	/// <summary>Null only until it is sealed (<see cref="Seal"/>).</summary>
	public DocHashes? Integrity { get; init; }

	/// <summary>A new identity: the UUIDv7 of <paramref name="at"/>, in lower case.</summary>
	public static string NewId(DateTimeOffset at) => Guid.CreateVersion7(at).ToString("D");

	/// <summary>An instant as the sidecars write it: UTC, to the second, with its <c>Z</c>.</summary>
	public static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	/// <summary>An instant of a sidecar, valid by its schema.</summary>
	public static DateTimeOffset ParseInstant(string text)
		=> DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

	static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

	/// <summary>The JSON of a sidecar, a <see cref="JsonException"/> if it is not JSON or repeats a property.</summary>
	public static JsonDocument Parse(string json)
	{
		try
		{
			return JsonDocument.Parse(json, Strict);
		}
		// looking for repeated properties reads their names, and one that is not UTF-16 fails there
		catch (InvalidOperationException ex)
		{
			throw new JsonException(ex.Message, ex);
		}
	}

	/// <summary>The sidecar <paramref name="sidecar"/>, valid by its schema (<see cref="DocsSchemas.Validate"/>).</summary>
	public static DocSidecar From(JsonElement sidecar)
	{
		string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;
		string? Optional(string name) => sidecar.TryGetProperty(name, out var value) ? value.GetString() : null;
		return new()
		{
			SchemaVersion = Text(sidecar, "schemaVersion"),
			Id = Text(sidecar, "id"),
			Type = Text(sidecar, "type"),
			Profile = Optional("profile"),
			Status = Text(sidecar, "status"),
			Title = Text(sidecar, "title"),
			Tags = sidecar.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(t => t.GetString()!).ToList() : [],
			CreatedAt = Text(sidecar, "createdAt"),
			UpdatedAt = Text(sidecar, "updatedAt"),
			CreatedBy = Text(sidecar, "createdBy"),
			Relations = sidecar.TryGetProperty("relations", out var relations)
				? relations.EnumerateArray().Select(r => new DocRelation(Text(r, "kind"), Text(r, "targetId"))).ToList()
				: [],
			Generation = sidecar.TryGetProperty("generation", out var generation) ? new(Text(generation, "recipe"), Text(generation, "scope")) : null,
			Integrity = sidecar.TryGetProperty("integrity", out var integrity) ? new(Text(integrity, "content"), Text(integrity, "metadata")) : null
		};
	}

	static readonly JsonWriterOptions Writer = new() { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>
	/// The sidecar as fx writes it: the properties in the order of the standard, without the ones it has not, the tags
	/// sorted and the relations by kind and target, indented with two spaces, LF, and a last line end.
	/// </summary>
	public string ToJson()
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, Writer))
		{
			writer.WriteStartObject();
			writer.WriteString("schemaVersion", SchemaVersion);
			writer.WriteString("id", Id);
			writer.WriteString("type", Type);
			if (Profile is not null)
				writer.WriteString("profile", Profile);
			writer.WriteString("status", Status);
			writer.WriteString("title", Title);
			if (Tags.Count > 0)
			{
				writer.WriteStartArray("tags");
				foreach (var tag in Tags.Order(StringComparer.Ordinal))
					writer.WriteStringValue(tag);
				writer.WriteEndArray();
			}
			writer.WriteString("createdAt", CreatedAt);
			writer.WriteString("updatedAt", UpdatedAt);
			writer.WriteString("createdBy", CreatedBy);
			if (Relations.Count > 0)
			{
				writer.WriteStartArray("relations");
				foreach (var relation in Relations.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.TargetId, StringComparer.Ordinal))
				{
					writer.WriteStartObject();
					writer.WriteString("kind", relation.Kind);
					writer.WriteString("targetId", relation.TargetId);
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
			}
			if (Generation is not null)
			{
				writer.WriteStartObject("generation");
				writer.WriteString("recipe", Generation.Recipe);
				writer.WriteString("scope", Generation.Scope);
				writer.WriteEndObject();
			}
			if (Integrity is not null)
			{
				writer.WriteStartObject("integrity");
				writer.WriteString("content", Integrity.Content);
				writer.WriteString("metadata", Integrity.Metadata);
				writer.WriteEndObject();
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
	}

	/// <summary>The hash of its metadata, as <see cref="ToJson"/> writes it.</summary>
	public string MetadataHash()
	{
		using var json = Parse(ToJson());
		return DocIntegrity.Metadata(json.RootElement);
	}

	/// <summary>The sidecar with its integrity: <paramref name="content"/>, the hash of its Markdown, and the hash of its metadata.</summary>
	public DocSidecar Seal(string content)
	{
		var unsealed = this with { Integrity = new(content, "") };
		return unsealed with { Integrity = new(content, unsealed.MetadataHash()) };
	}
}
