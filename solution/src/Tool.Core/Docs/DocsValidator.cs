using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A schema of a <see cref="DocsValidator"/>: its name among the others (its path), its <c>$id</c> and its JSON.</summary>
public sealed record DocsSchema(string Name, Uri Id, JsonElement Root);

/// <summary>
/// fx's own validator of JSON Schema 2020-12 (plan U, ADR "el validador es propio"): the keywords the schemas of the
/// standard use, and no other. A schema with a keyword it does not know is refused when it is loaded, so a schema that
/// needs one more needs it here first. What it knows:
/// <list type="bullet">
/// <item><c>$ref</c> between the schemas, by their <c>$id</c> and a JSON pointer; <c>$defs</c>; <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c> and <c>not</c>.</item>
/// <item><c>type</c>, <c>enum</c> and <c>const</c> (numbers equal by their value).</item>
/// <item>Objects: <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>unevaluatedProperties</c>, <c>propertyNames</c> and <c>minProperties</c>.</item>
/// <item>Arrays: <c>items</c>, <c>minItems</c> and <c>uniqueItems</c>.</item>
/// <item>Strings: <c>minLength</c> and <c>maxLength</c> (in code points), <c>pattern</c> and <c>format</c>, an assertion: <c>uuid</c> and <c>date-time</c>.</item>
/// <item>Annotations, which do nothing: <c>$schema</c> (2020-12), <c>$id</c>, <c>$comment</c>, <c>title</c>, <c>description</c> and those of the standard (<c>x-</c>).</item>
/// </list>
/// Its results are those of the standard. Where it reports more freely: what a failed <c>allOf</c> or <c>$ref</c> evaluated still counts for
/// <c>unevaluatedProperties</c> (the whole fails anyway), so an unknown property is reported only when it is one; and the branches of an
/// <c>anyOf</c>, a <c>oneOf</c> or a <c>not</c> give one problem, not theirs.
/// </summary>
public sealed partial class DocsValidator
{
	public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

	/// <summary>The prefix of the keys of its messages.</summary>
	const string Key = "docs.schema";

	static readonly HashSet<string> Types = ["array", "boolean", "integer", "null", "number", "object", "string"];
	static readonly HashSet<string> Formats = ["date-time", "uuid"];

	readonly Dictionary<string, DocsSchema> _byId = new(StringComparer.Ordinal);
	readonly Dictionary<(string Base, string Reference), (DocsSchema Resource, JsonElement Schema)> _references = [];
	readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

	/// <summary>
	/// The validator of the schemas <paramref name="texts"/> (their name and their JSON), checked: each with its <c>$schema</c> and an
	/// absolute <c>$id</c>, every keyword known, every <c>$ref</c> leading to a schema, every pattern a regular expression. An
	/// <see cref="InvalidOperationException"/> says what is wrong, and where.
	/// </summary>
	public DocsValidator(IEnumerable<KeyValuePair<string, string>> texts)
	{
		var schemas = new SortedDictionary<string, DocsSchema>(StringComparer.Ordinal);
		foreach (var (name, text) in texts)
		{
			JsonElement root;
			using (var document = JsonDocument.Parse(text))
				root = document.RootElement.Clone();
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$schema", out var dialect) || dialect.ValueKind != JsonValueKind.String
			    || dialect.GetString() != Dialect)
				throw Refused(name, "", $"its $schema must be {Dialect}");
			if (!root.TryGetProperty("$id", out var id) || id.ValueKind != JsonValueKind.String || !Uri.TryCreate(id.GetString(), UriKind.Absolute, out var uri)
			    || uri.Fragment.Length > 0)
				throw Refused(name, "", "its $id must be an absolute URI, without a fragment");
			var schema = new DocsSchema(name, uri, root);
			if (!_byId.TryAdd(uri.AbsoluteUri, schema))
				throw Refused(name, "", $"another schema has its $id {uri.AbsoluteUri}");
			schemas[name] = schema;
		}
		Schemas = schemas;
		foreach (var schema in schemas.Values)
			Check(schema, schema.Root, "", true);
	}

	/// <summary>The schemas, by their name.</summary>
	public IReadOnlyDictionary<string, DocsSchema> Schemas { get; }

	static InvalidOperationException Refused(string name, string at, string why) => new($"The schema '{name}'{(at.Length > 0 ? $", at '{at}'" : "")}: {why}.");

	static string Child(string pointer, string name) => $"{pointer}/{name.Replace("~", "~0").Replace("/", "~1")}";

	static string Child(string pointer, int index) => $"{pointer}/{index.ToString(CultureInfo.InvariantCulture)}";

	void Check(DocsSchema resource, JsonElement schema, string at, bool root)
	{
		if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
			return;
		if (schema.ValueKind != JsonValueKind.Object)
			throw Refused(resource.Name, at, "a schema is an object or a boolean");
		foreach (var keyword in schema.EnumerateObject().Where(k => !k.Name.StartsWith("x-", StringComparison.Ordinal)))
		{
			var here = Child(at, keyword.Name);
			var value = keyword.Value;
			switch (keyword.Name)
			{
				case "$comment" or "title" or "description":
					break;
				case "$schema" or "$id":
					if (!root)
						throw Refused(resource.Name, here, "only at the root of a schema");
					break;
				case "$ref":
				{
					var reference = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Refused(resource.Name, here, "it must be a string");
					_references[(resource.Id.AbsoluteUri, reference)] = Resolve(resource, reference) ?? throw Refused(resource.Name, here, $"'{reference}' leads to no schema");
					break;
				}
				case "$defs" or "properties":
					if (value.ValueKind != JsonValueKind.Object)
						throw Refused(resource.Name, here, "it must be an object of schemas");
					foreach (var property in value.EnumerateObject())
						Check(resource, property.Value, Child(here, property.Name), false);
					break;
				case "allOf" or "anyOf" or "oneOf":
				{
					if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
						throw Refused(resource.Name, here, "it must be a list of schemas, not empty");
					var index = 0;
					foreach (var item in value.EnumerateArray())
						Check(resource, item, Child(here, index++), false);
					break;
				}
				case "not" or "items" or "additionalProperties" or "unevaluatedProperties" or "propertyNames":
					Check(resource, value, here, false);
					break;
				case "type":
				{
					var names = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
					if (names.Count == 0 || names.Any(n => n.ValueKind != JsonValueKind.String || !Types.Contains(n.GetString()!)))
						throw Refused(resource.Name, here, $"it must be one of {string.Join(", ", Types)}, or a list of them");
					break;
				}
				case "enum":
					if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
						throw Refused(resource.Name, here, "it must be a list of values, not empty");
					break;
				case "const":
					break;
				case "required":
					if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.String))
						throw Refused(resource.Name, here, "it must be a list of names");
					break;
				case "minProperties" or "minItems" or "minLength" or "maxLength":
					if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
						throw Refused(resource.Name, here, "it must be a whole number, not negative");
					break;
				case "uniqueItems":
					if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
						throw Refused(resource.Name, here, "it must be true or false");
					break;
				case "pattern":
				{
					var pattern = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Refused(resource.Name, here, "it must be a string");
					try
					{
						_patterns[pattern] = Pattern(pattern);
					}
					catch (ArgumentException ex)
					{
						throw Refused(resource.Name, here, $"'{pattern}' is not a regular expression ({ex.Message})");
					}
					break;
				}
				case "format":
					if (value.ValueKind != JsonValueKind.String || !Formats.Contains(value.GetString()!))
						throw Refused(resource.Name, here, $"fx knows the formats {string.Join(", ", Formats)}");
					break;
				default:
					throw Refused(resource.Name, here, "fx's validator does not know this keyword");
			}
		}
	}

	/// <summary>The schema <paramref name="reference"/> leads to from <paramref name="resource"/>: another by its <c>$id</c>, and inside it by a JSON pointer.</summary>
	(DocsSchema, JsonElement)? Resolve(DocsSchema resource, string reference)
	{
		if (!Uri.TryCreate(resource.Id, reference, out var target) || !_byId.TryGetValue(target.GetLeftPart(UriPartial.Query), out var found))
			return null;
		var schema = found.Root;
		var pointer = Uri.UnescapeDataString(target.Fragment.Length > 0 ? target.Fragment[1..] : "");
		if (pointer.Length == 0)
			return (found, schema);
		// an anchor (#name) is not a pointer: not known
		if (pointer[0] != '/')
			return null;
		foreach (var token in pointer[1..].Split('/'))
		{
			var name = token.Replace("~1", "/").Replace("~0", "~");
			if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(name, out var next))
				schema = next;
			else if (schema.ValueKind == JsonValueKind.Array && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < schema.GetArrayLength())
				schema = schema[index];
			else
				return null;
		}
		return (found, schema);
	}

	/// <summary>
	/// A pattern of a schema (ECMA-262) as a .NET regular expression, with the meaning it has in a schema where they differ: <c>$</c>
	/// only at the very end (not before a last line end), <c>.</c> not across any line end, and <c>\d</c> only the digits 0 to 9.
	/// </summary>
	static Regex Pattern(string pattern)
	{
		var b = new StringBuilder();
		var inClass = false;
		for (var i = 0; i < pattern.Length; i++)
		{
			var c = pattern[i];
			if (c == '\\' && i + 1 < pattern.Length)
			{
				var next = pattern[++i];
				if (next == 'd')
					b.Append(inClass ? "0-9" : "[0-9]");
				else
					b.Append(c).Append(next);
			}
			else if (inClass)
			{
				inClass = c != ']';
				b.Append(c);
			}
			else
			{
				inClass = c == '[';
				b.Append(c switch
				{
					'$' => @"\z",
					'.' => @"[^\n\r  ]",
					_ => c.ToString()
				});
			}
		}
		return new(b.ToString(), RegexOptions.CultureInvariant);
	}

	/// <summary>The problems of <paramref name="instance"/> against <paramref name="schema"/>, ordered by their place; none, an empty list.</summary>
	public IReadOnlyList<DocsSchemaError> Validate(DocsSchema schema, JsonElement instance)
	{
		var errors = new List<DocsSchemaError>();
		Evaluate(schema, schema.Root, instance, "", errors, null);
		return errors.Distinct().OrderBy(e => e.Pointer, StringComparer.Ordinal).ThenBy(e => e.Keyword, StringComparer.Ordinal).ToList();
	}

	/// <summary>
	/// Whether <paramref name="instance"/>, at <paramref name="pointer"/>, is valid by <paramref name="schema"/> (inside <paramref name="resource"/>),
	/// adding its problems to <paramref name="errors"/> (if any) and the properties of the instance it evaluated to <paramref name="evaluated"/>
	/// (if any), for an <c>unevaluatedProperties</c> around it.
	/// </summary>
	bool Evaluate(DocsSchema resource, JsonElement schema, JsonElement instance, string pointer, List<DocsSchemaError>? errors, HashSet<string>? evaluated)
	{
		if (schema.ValueKind == JsonValueKind.True)
			return true;
		if (schema.ValueKind == JsonValueKind.False)
		{
			errors?.Add(new(pointer, "false", new($"{Key}.false")));
			return false;
		}
		var valid = true;
		var properties = instance.ValueKind == JsonValueKind.Object ? new HashSet<string>(StringComparer.Ordinal) : null;
		foreach (var keyword in schema.EnumerateObject())
		{
			var value = keyword.Value;
			switch (keyword.Name)
			{
				case "$ref":
				{
					var (target, subschema) = _references[(resource.Id.AbsoluteUri, value.GetString()!)];
					valid &= Evaluate(target, subschema, instance, pointer, errors, properties);
					break;
				}
				case "allOf":
					foreach (var item in value.EnumerateArray())
						valid &= Evaluate(resource, item, instance, pointer, errors, properties);
					break;
				case "anyOf" or "oneOf":
				{
					// what the branches that match evaluated; if none does, what all evaluated (the whole fails anyway)
					var matched = 0;
					var ofMatched = new HashSet<string>(StringComparer.Ordinal);
					var ofAll = new HashSet<string>(StringComparer.Ordinal);
					foreach (var item in value.EnumerateArray())
					{
						var seen = properties is null ? null : new HashSet<string>(StringComparer.Ordinal);
						if (Evaluate(resource, item, instance, pointer, null, seen))
						{
							matched++;
							ofMatched.UnionWith(seen ?? []);
						}
						ofAll.UnionWith(seen ?? []);
					}
					properties?.UnionWith(matched > 0 ? ofMatched : ofAll);
					if (keyword.Name == "anyOf" ? matched == 0 : matched != 1)
						Fail(keyword.Name, keyword.Name == "anyOf" ? new($"{Key}.any-of") : new($"{Key}.one-of", matched));
					break;
				}
				case "not":
					if (Evaluate(resource, value, instance, pointer, null, null))
						Fail("not", new($"{Key}.not"));
					break;
				case "type":
				{
					var names = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(n => n.GetString()!).ToList() : [value.GetString()!];
					if (!names.Any(n => Is(instance, n)))
						Fail("type", new($"{Key}.type", string.Join(" | ", names), Kind(instance)));
					break;
				}
				case "enum":
					if (!value.EnumerateArray().Any(v => Same(v, instance)))
						Fail("enum", new($"{Key}.enum", string.Join(", ", value.EnumerateArray().Select(v => v.GetRawText()))));
					break;
				case "const":
					if (!Same(value, instance))
						Fail("const", new($"{Key}.const", value.GetRawText()));
					break;
				case "required" when properties is not null:
				{
					var missing = value.EnumerateArray().Select(n => n.GetString()!).Where(n => !instance.TryGetProperty(n, out _)).ToList();
					if (missing.Count > 0)
						Fail("required", new($"{Key}.required", string.Join(", ", missing)));
					break;
				}
				case "properties" when properties is not null:
					foreach (var property in instance.EnumerateObject())
						if (value.TryGetProperty(property.Name, out var subschema))
						{
							properties.Add(property.Name);
							valid &= Evaluate(resource, subschema, property.Value, Child(pointer, property.Name), errors, null);
						}
					break;
				case "additionalProperties" when properties is not null:
				{
					var known = schema.TryGetProperty("properties", out var named) ? named : default;
					foreach (var property in instance.EnumerateObject())
						if (known.ValueKind != JsonValueKind.Object || !known.TryGetProperty(property.Name, out _))
						{
							properties.Add(property.Name);
							valid &= Rest(resource, value, property, pointer, "additionalProperties", errors);
						}
					break;
				}
				case "propertyNames" when properties is not null:
					foreach (var property in instance.EnumerateObject())
						if (!Evaluate(resource, value, String(property.Name), Child(pointer, property.Name), null, null))
						{
							valid = false;
							errors?.Add(new(Child(pointer, property.Name), "propertyNames", new($"{Key}.property-name", property.Name)));
						}
					break;
				case "minProperties" when properties is not null:
					if (instance.EnumerateObject().Count() < value.GetInt32())
						Fail("minProperties", new($"{Key}.min-properties", value.GetInt32()));
					break;
				case "items" when instance.ValueKind == JsonValueKind.Array:
				{
					var index = 0;
					foreach (var item in instance.EnumerateArray())
						valid &= Evaluate(resource, value, item, Child(pointer, index++), errors, null);
					break;
				}
				case "minItems" when instance.ValueKind == JsonValueKind.Array:
					if (instance.GetArrayLength() < value.GetInt32())
						Fail("minItems", new($"{Key}.min-items", value.GetInt32()));
					break;
				case "uniqueItems" when instance.ValueKind == JsonValueKind.Array && value.ValueKind == JsonValueKind.True:
					if (Repeated(instance.EnumerateArray().ToList()) is { } pair)
						Fail("uniqueItems", new($"{Key}.unique-items", pair.First, pair.Second));
					break;
				case "pattern" or "minLength" or "maxLength" or "format" when instance.ValueKind == JsonValueKind.String:
				{
					if (Read(instance) is not { } text)
						Fail("type", new($"{Key}.unicode"));
					else if (keyword.Name == "pattern" && !_patterns[value.GetString()!].IsMatch(text))
						Fail("pattern", new($"{Key}.pattern", value.GetString()));
					else if (keyword.Name == "minLength" && Length(text) < value.GetInt32())
						Fail("minLength", new($"{Key}.min-length", value.GetInt32()));
					else if (keyword.Name == "maxLength" && Length(text) > value.GetInt32())
						Fail("maxLength", new($"{Key}.max-length", value.GetInt32()));
					else if (keyword.Name == "format" && !Formatted(value.GetString()!, text))
						Fail("format", new($"{Key}.format.{value.GetString()}"));
					break;
				}
			}
		}
		// after the rest, which say what they evaluated
		if (properties is not null && schema.TryGetProperty("unevaluatedProperties", out var unevaluated))
			foreach (var property in instance.EnumerateObject())
				if (properties.Add(property.Name))
					valid &= Rest(resource, unevaluated, property, pointer, "unevaluatedProperties", errors);
		if (properties is not null)
			evaluated?.UnionWith(properties);
		return valid;

		void Fail(string keyword, FxText message)
		{
			valid = false;
			errors?.Add(new(pointer, keyword, message));
		}
	}

	/// <summary>A property that only <paramref name="keyword"/> covers, against its schema: if that is <c>false</c>, the property is not allowed there.</summary>
	bool Rest(DocsSchema resource, JsonElement schema, JsonProperty property, string pointer, string keyword, List<DocsSchemaError>? errors)
	{
		var at = Child(pointer, property.Name);
		if (schema.ValueKind != JsonValueKind.False)
			return Evaluate(resource, schema, property.Value, at, errors, null);
		errors?.Add(new(at, keyword, new($"{Key}.not-allowed")));
		return false;
	}

	static string Kind(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Object => "object",
		JsonValueKind.Array => "array",
		JsonValueKind.String => "string",
		JsonValueKind.Number => "number",
		JsonValueKind.True or JsonValueKind.False => "boolean",
		_ => "null"
	};

	static bool Is(JsonElement value, string type) => type == "integer"
		? value.ValueKind == JsonValueKind.Number && (value.TryGetDecimal(out var d) ? d == decimal.Truncate(d) : value.TryGetDouble(out var x) && Math.Floor(x) == x)
		: Kind(value) == type;

	/// <summary>Whether two values are the same JSON: numbers by their value (<c>1</c> and <c>1.0</c>), objects whatever the order of their properties.</summary>
	static bool Same(JsonElement a, JsonElement b)
	{
		if (a.ValueKind != b.ValueKind)
			return false;
		return a.ValueKind switch
		{
			JsonValueKind.Number => a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y
				: a.TryGetDouble(out var u) && b.TryGetDouble(out var v) ? u == v
				: a.GetRawText() == b.GetRawText(),
			JsonValueKind.String => Read(a) is { } s && Read(b) is { } t ? s == t : a.GetRawText() == b.GetRawText(),
			JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second)),
			JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
			                        && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var q) && Same(p.Value, q)),
			_ => true
		};
	}

	/// <summary>The first two items of <paramref name="items"/> that are the same, by their indexes.</summary>
	static (int First, int Second)? Repeated(IReadOnlyList<JsonElement> items)
	{
		for (var i = 1; i < items.Count; i++)
			for (var j = 0; j < i; j++)
				if (Same(items[j], items[i]))
					return (j, i);
		return null;
	}

	/// <summary>The text of a string; null if it is not Unicode (a lone surrogate, written as <c>\ud800</c>).</summary>
	static string? Read(JsonElement value)
	{
		try
		{
			return value.GetString();
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>A name, as a JSON string, for <c>propertyNames</c>.</summary>
	static JsonElement String(string text)
	{
		using var document = JsonDocument.Parse($"\"{JsonEncodedText.Encode(text).Value}\"");
		return document.RootElement.Clone();
	}

	/// <summary>The length of a text in code points, as JSON Schema counts it: a character outside the basic plane (an emoji) is one.</summary>
	static int Length(string text)
	{
		var length = 0;
		for (var i = 0; i < text.Length; i++, length++)
			if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				i++;
		return length;
	}

	[GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\z")]
	private static partial Regex Uuid();

	[GeneratedRegex(@"^(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})[Tt](?<h>[0-9]{2}):(?<mi>[0-9]{2}):(?<s>[0-9]{2})(?:\.[0-9]+)?(?:[Zz]|[+-](?<oh>[0-9]{2}):(?<om>[0-9]{2}))\z")]
	private static partial Regex DateTimePattern();

	/// <summary>
	/// Whether <paramref name="text"/> has the format <paramref name="format"/>: a <c>uuid</c> of RFC 9562 (its hex groups), or a <c>date-time</c>
	/// of RFC 3339 with a real day and time, which .NET reads too (so without a leap second, which the module could not read after).
	/// </summary>
	static bool Formatted(string format, string text)
	{
		if (format == "uuid")
			return Uuid().IsMatch(text);
		if (DateTimePattern().Match(text) is not { Success: true } m)
			return false;
		int Part(string name) => m.Groups[name].Success ? int.Parse(m.Groups[name].ValueSpan, CultureInfo.InvariantCulture) : 0;
		var (year, month) = (Part("y"), Part("mo"));
		return year >= 1 && month is >= 1 and <= 12 && Part("d") >= 1 && Part("d") <= DateTime.DaysInMonth(year, month) && Part("h") <= 23 && Part("mi") <= 59
		       && Part("s") <= 59 && Part("oh") <= 23 && Part("om") <= 59
		       && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
	}
}
