using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Core.Docs;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// fx's own validator of JSON Schema (plan U, ADR "el validador es propio"): each keyword the schemas of the standard use,
/// <c>unevaluatedProperties</c> through <c>$ref</c>, <c>allOf</c> and <c>anyOf</c>, and the schemas it refuses to load.
/// </summary>
public sealed class DocsValidatorTest
{
	const string Root = "https://schemas.test.invalid/";

	static string Schema(string name, string body) => $$"""{ "$schema": "{{DocsValidator.Dialect}}", "$id": "{{Root}}{{name}}", {{body}} }""";

	static DocsValidator Validator(params (string Name, string Body)[] schemas)
		=> new(schemas.Select(s => new KeyValuePair<string, string>(s.Name, Schema(s.Name, s.Body))));

	static IReadOnlyList<DocsSchemaError> Errors(DocsValidator validator, string instance, string name = "s.json")
		=> validator.Validate(validator.Schemas[name], JsonDocument.Parse(instance).RootElement);

	static IReadOnlyList<(string, string)> Places(IReadOnlyList<DocsSchemaError> errors) => errors.Select(e => (e.Pointer, e.Keyword)).ToList();

	[Theory(DisplayName = "each keyword: a valid value passes, and an invalid one is a problem at its place, with its text")]
	[InlineData("\"type\": \"string\"", "\"a\"", "1", "", "type")]
	[InlineData("\"type\": [\"string\", \"number\"]", "1", "true", "", "type")]
	[InlineData("\"type\": \"integer\"", "2.0", "2.5", "", "type")]
	[InlineData("\"enum\": [\"a\", 1]", "1.0", "\"b\"", "", "enum")]
	[InlineData("\"const\": { \"a\": [1, 2] }", "{ \"a\": [1.0, 2] }", "{ \"a\": [2, 1] }", "", "const")]
	[InlineData("\"required\": [\"a\"]", "{ \"a\": 1 }", "{ \"b\": 1 }", "", "required")]
	[InlineData("\"properties\": { \"a\": { \"type\": \"string\" } }", "{ \"a\": \"x\", \"b\": 1 }", "{ \"a\": 1 }", "/a", "type")]
	[InlineData("\"properties\": { \"a/b\": { \"type\": \"string\" } }", "{ \"a/b\": \"x\" }", "{ \"a/b\": 1 }", "/a~1b", "type")]
	[InlineData("\"properties\": { \"a\": true }, \"additionalProperties\": false", "{ \"a\": 1 }", "{ \"a\": 1, \"b\": 2 }", "/b", "additionalProperties")]
	[InlineData("\"additionalProperties\": { \"type\": \"number\" }", "{ \"a\": 1 }", "{ \"a\": \"x\" }", "/a", "type")]
	[InlineData("\"propertyNames\": { \"pattern\": \"^[a-z]+$\" }", "{ \"ab\": 1 }", "{ \"Ab\": 1 }", "/Ab", "propertyNames")]
	[InlineData("\"minProperties\": 1", "{ \"a\": 1 }", "{}", "", "minProperties")]
	[InlineData("\"items\": { \"type\": \"number\" }", "[1, 2]", "[1, \"x\"]", "/1", "type")]
	[InlineData("\"minItems\": 2", "[1, 2]", "[1]", "", "minItems")]
	[InlineData("\"uniqueItems\": true", "[1, 2, { \"a\": 1 }, { \"a\": 2 }]", "[{ \"a\": 1, \"b\": 2 }, 1, { \"b\": 2, \"a\": 1.0 }]", "", "uniqueItems")]
	[InlineData("\"minLength\": 2", "\"\ud83d\ude00\ud83d\ude00\"", "\"\ud83d\ude00\"", "", "minLength")]
	[InlineData("\"maxLength\": 1", "\"\ud83d\ude00\"", "\"ab\"", "", "maxLength")]
	[InlineData("\"minLength\": 1", "\"a\"", "\"\\ud800\"", "", "type")]
	[InlineData("\"pattern\": \"^a.c$\"", "\"abc\"", "\"abc\\n\"", "", "pattern")]
	[InlineData("\"pattern\": \"^a.c$\"", "\"abc\"", "\"a\\rc\"", "", "pattern")]
	[InlineData("\"pattern\": \"^\\\\d+$\"", "\"12\"", "\"\u0661\u0662\"", "", "pattern")]
	[InlineData("\"pattern\": \"\\\\S\"", "\" a \"", "\" \"", "", "pattern")]
	[InlineData("\"format\": \"uuid\"", "\"0192a8f0-7c3e-7b4a-9d2e-3f4a5b6c7d8e\"", "\"0192a8f0-7c3e-7b4a-9d2e\"", "", "format")]
	[InlineData("\"format\": \"date-time\"", "\"2024-02-29T23:59:59.5+01:00\"", "\"2023-02-29T10:00:00Z\"", "", "format")]
	[InlineData("\"format\": \"date-time\"", "\"2026-10-21T10:00:00Z\"", "\"2026-12-31T23:59:60Z\"", "", "format")]
	[InlineData("\"format\": \"date-time\"", "\"2026-10-21T10:00:00Z\"", "\"2026-10-21 10:00:00Z\"", "", "format")]
	[InlineData("\"allOf\": [{ \"type\": \"string\" }, { \"minLength\": 2 }]", "\"ab\"", "\"a\"", "", "minLength")]
	[InlineData("\"anyOf\": [{ \"type\": \"string\" }, { \"type\": \"number\" }]", "1", "true", "", "anyOf")]
	[InlineData("\"oneOf\": [{ \"type\": \"number\" }, { \"type\": \"integer\" }]", "1.5", "1", "", "oneOf")]
	[InlineData("\"not\": { \"type\": \"null\" }", "1", "null", "", "not")]
	[InlineData("\"$defs\": { \"n\": { \"type\": \"number\" } }, \"properties\": { \"a\": { \"$ref\": \"#/$defs/n\" } }", "{ \"a\": 1 }", "{ \"a\": \"x\" }", "/a", "type")]
	[InlineData("\"properties\": { \"a\": false }", "{}", "{ \"a\": 1 }", "/a", "false")]
	public void Keyword(string body, string valid, string invalid, string pointer, string keyword)
	{
		var validator = Validator(("s.json", body));
		Assert.Empty(Errors(validator, valid));
		var errors = Errors(validator, invalid);
		Assert.Contains((pointer, keyword), Places(errors));
		Assert.All(errors, e => Assert.DoesNotContain("docs.schema", e.Message.English, StringComparison.Ordinal));
		Assert.All(errors, e => Assert.DoesNotContain("docs.schema", e.Message.ToString(new("es")), StringComparison.Ordinal));
	}

	[Fact(DisplayName = "unevaluatedProperties sees what $ref and allOf evaluated, and of an anyOf the branches that match; an unknown property, alone")]
	public void Unevaluated()
	{
		var validator = Validator(
			("base.json", "\"properties\": { \"a\": { \"type\": \"number\" } }"),
			("s.json", "\"allOf\": [{ \"$ref\": \"base.json\" }, { \"properties\": { \"b\": true } }], \"unevaluatedProperties\": false"),
			("any.json", "\"anyOf\": [{ \"properties\": { \"a\": { \"type\": \"number\" } } }, { \"properties\": { \"b\": true } }], \"unevaluatedProperties\": false"));
		Assert.Empty(Errors(validator, "{ \"a\": 1, \"b\": 2 }"));
		Assert.Equal([("/c", "unevaluatedProperties")], Places(Errors(validator, "{ \"a\": 1, \"c\": 2 }")));
		// a property of the base that fails is still known: no cascade of unexpected properties
		Assert.Equal([("/a", "type")], Places(Errors(validator, "{ \"a\": \"x\", \"b\": 2 }")));
		Assert.Empty(Errors(validator, "{ \"a\": 1, \"b\": 2 }", "any.json"));
		// only the second branch matches: what the first evaluated does not count
		Assert.Equal([("/a", "unevaluatedProperties")], Places(Errors(validator, "{ \"a\": \"x\", \"b\": 2 }", "any.json")));
	}

	[Theory(DisplayName = "a schema with what the validator does not know is refused when it is loaded")]
	[InlineData("\"minimum\": 1")]
	[InlineData("\"format\": \"email\"")]
	[InlineData("\"type\": \"text\"")]
	[InlineData("\"type\": []")]
	[InlineData("\"$ref\": \"other.json\"")]
	[InlineData("\"$ref\": \"#/$defs/none\"")]
	[InlineData("\"$ref\": \"#anchor\"")]
	[InlineData("\"pattern\": \"(\"")]
	[InlineData("\"minLength\": -1")]
	[InlineData("\"enum\": []")]
	[InlineData("\"allOf\": []")]
	[InlineData("\"properties\": { \"a\": { \"$id\": \"https://schemas.test.invalid/a\" } }")]
	[InlineData("\"properties\": { \"a\": 1 }")]
	public void Refused(string body) => Assert.Throws<InvalidOperationException>(() => Validator(("s.json", body)));

	[Fact(DisplayName = "a schema needs the dialect 2020-12 and an absolute $id of its own; the annotations of the standard (x-) are not read")]
	public void Loaded()
	{
		Assert.Throws<InvalidOperationException>(() => new DocsValidator([new("s.json", """{ "$id": "https://schemas.test.invalid/s.json" }""")]));
		Assert.Throws<InvalidOperationException>(() => new DocsValidator([new("s.json", """{ "$schema": "http://json-schema.org/draft-07/schema#", "$id": "https://schemas.test.invalid/s.json" }""")]));
		Assert.Throws<InvalidOperationException>(() => new DocsValidator([new("s.json", $$"""{ "$schema": "{{DocsValidator.Dialect}}", "$id": "s.json" }""")]));
		var same = Schema("s.json", "\"title\": \"one\"");
		Assert.Throws<InvalidOperationException>(() => new DocsValidator([new("s.json", same), new("t.json", same)]));
		var validator = Validator(("s.json", "\"x-docs-anything\": { \"minimum\": 1 }, \"title\": \"t\", \"$comment\": \"c\""));
		Assert.Empty(Errors(validator, "1"));
	}
}
