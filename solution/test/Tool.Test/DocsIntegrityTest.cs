using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Fuxion.Tool.Core.Docs;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The identity and the integrity of a document (plan U, U4 step 2): UUIDv7, the hash of the Markdown, the canonical form
/// of RFC 8785 with its test vectors, and the sidecar written in the order of the standard.
/// </summary>
public sealed class DocsIntegrityTest
{
	static readonly string Content = "sha256:" + new string('a', 64);

	/// <summary>A sidecar with every property, its tags and relations out of order.</summary>
	static DocSidecar Register() => new()
	{
		Id = "0192a8f0-7c3e-7b4a-9d2e-3f4a5b6c7d8e",
		Type = "register",
		Profile = "backlog",
		Status = "active",
		Title = "Índice de ideas",
		Tags = ["ideas", "backlog"],
		CreatedAt = "2026-10-18T10:00:00Z",
		UpdatedAt = "2026-10-21T10:00:00Z",
		CreatedBy = "editor",
		Relations = [new("related-to", "0192a8f1-0000-7000-8000-000000000002"), new("depends-on", "0192a8f1-0000-7000-8000-000000000001")],
		Generation = new("docs.backlog", "_docs/ideas"),
		Integrity = new(Content, "sha256:" + new string('b', 64))
	};

	static string Canonical(string json)
	{
		using var document = DocSidecar.Parse(json);
		return Jcs.Text(document.RootElement);
	}

	static string Metadata(string json)
	{
		using var document = DocSidecar.Parse(json);
		return DocIntegrity.Metadata(document.RootElement);
	}

	// identity

	[Fact(DisplayName = "a new identity is a UUIDv7 in lower case, with the instant in its first 48 bits, and sorts by it")]
	public void NewId()
	{
		var at = new DateTimeOffset(2026, 10, 21, 10, 30, 15, 123, TimeSpan.Zero);
		var id = DocSidecar.NewId(at);
		var guid = Guid.Parse(id);
		Assert.Equal(7, guid.Version);
		Assert.Equal(0b10, guid.Variant >> 2);
		Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
		Assert.Equal(at.ToUnixTimeMilliseconds(), Convert.ToInt64(id.Replace("-", "")[..12], 16));
		Assert.NotEqual(id, DocSidecar.NewId(at));
		Assert.True(string.CompareOrdinal(id, DocSidecar.NewId(at.AddMilliseconds(1))) < 0);
	}

	[Fact(DisplayName = "an instant is written in UTC to the second with its Z; at the end of a month, the month of UTC")]
	public void Instant()
	{
		Assert.Equal("2026-11-01T01:59:59Z", DocSidecar.Instant(new DateTimeOffset(2026, 10, 31, 23, 59, 59, 999, TimeSpan.FromHours(-2))));
		Assert.Equal("2026-10-31T22:00:00Z", DocSidecar.Instant(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(2))));
		Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), DocSidecar.ParseInstant("2026-10-18T10:00:00Z"));
		Assert.Equal(TimeSpan.Zero, DocSidecar.ParseInstant("2026-10-18T10:00:00.5Z").Offset);
	}

	// the hash of the Markdown

	[Theory(DisplayName = "the hash of the content: SHA-256 of the UTF-8 text (independent values)")]
	[InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
	[InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
	[InlineData("# Título\n\nTexto.\n", "707d68c8482168bf561b7b6b0f8876bfff72cbc2d3e728cda76c06a53db8a27d")]
	public void ContentHash(string text, string hex)
	{
		Assert.Equal($"sha256:{hex}", DocIntegrity.Content(text));
		Assert.Equal($"sha256:{hex}", DocIntegrity.Content(Encoding.UTF8.GetBytes(text)));
	}

	[Fact(DisplayName = "the hash of the content: the same without BOM and with CRLF or CR; the spaces and the last line end count")]
	public void ContentNormalized()
	{
		var lf = DocIntegrity.Content("# Título\n\nTexto.\n");
		Assert.Equal(lf, DocIntegrity.Content("# Título\r\n\r\nTexto.\r\n"));
		Assert.Equal(lf, DocIntegrity.Content("# Título\r\rTexto.\r"));
		Assert.Equal(lf, DocIntegrity.Content([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Título\r\n\r\nTexto.\r\n")]));
		Assert.Equal(lf, DocIntegrity.Content("\uFEFF# Título\n\nTexto.\n"));
		Assert.NotEqual(lf, DocIntegrity.Content("# Título\n\nTexto."));
		Assert.NotEqual(lf, DocIntegrity.Content("# Título \n\nTexto.\n"));
		// only one byte order mark goes, the first
		Assert.NotEqual(lf, DocIntegrity.Content("\uFEFF\uFEFF# Título\n\nTexto.\n"));
	}

	[Fact(DisplayName = "the hash of the content: a Markdown that is not UTF-8 has none")]
	public void ContentNotUtf8() => Assert.Throws<DecoderFallbackException>(() => DocIntegrity.Content([0x23, 0x20, 0xC3, 0x28]));

	// RFC 8785

	[Fact(DisplayName = "RFC 8785, 3.2.2 to 3.2.4: the sample, sorted and in its exact UTF-8 bytes")]
	public void RfcSample()
	{
		const string input = """
			{
			  "numbers": [333333333.33333329, 1E30, 4.50,
			              2e-3, 0.000000000000000000000000001],
			  "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
			  "literals": [null, true, false]
			}
			""";
		const string bytes = """
			7b 22 6c 69 74 65 72 61 6c 73 22 3a 5b 6e 75 6c 6c 2c 74 72
			75 65 2c 66 61 6c 73 65 5d 2c 22 6e 75 6d 62 65 72 73 22 3a
			5b 33 33 33 33 33 33 33 33 33 2e 33 33 33 33 33 33 33 2c 31
			65 2b 33 30 2c 34 2e 35 2c 30 2e 30 30 32 2c 31 65 2d 32 37
			5d 2c 22 73 74 72 69 6e 67 22 3a 22 e2 82 ac 24 5c 75 30 30
			30 66 5c 6e 41 27 42 5c 22 5c 5c 5c 5c 5c 22 2f 22 7d
			""";
		using var document = DocSidecar.Parse(input);
		Assert.Equal(string.Concat(bytes.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)),
			Convert.ToHexStringLower(Jcs.Canonicalize(document.RootElement)));
		Assert.Equal("""{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""",
			Jcs.Text(document.RootElement));
	}

	[Fact(DisplayName = "RFC 8785, 3.2.3: the properties sorted by their UTF-16 code units, at every level")]
	public void RfcSorting()
	{
		const string input = """
			{
			  "\u20ac": "Euro Sign",
			  "\r": "Carriage Return",
			  "\ufb33": "Hebrew Letter Dalet With Dagesh",
			  "1": "One",
			  "\ud83d\ude00": "Emoji: Grinning Face",
			  "\u0080": "Control",
			  "\u00f6": "Latin Small Letter O With Diaeresis"
			}
			""";
		using var document = DocSidecar.Parse($"[{input}]");
		using var sorted = JsonDocument.Parse(Jcs.Canonicalize(document.RootElement));
		Assert.Equal(["Carriage Return", "One", "Control", "Latin Small Letter O With Diaeresis", "Euro Sign", "Emoji: Grinning Face", "Hebrew Letter Dalet With Dagesh"],
			sorted.RootElement[0].EnumerateObject().Select(p => p.Value.GetString()));
		Assert.Equal("""{"a":{"b":1,"c":[{"d":2,"e":3}]}}""", Canonical("""{ "a": { "c": [ { "e": 3, "d": 2 } ], "b": 1 } }"""));
	}

	[Theory(DisplayName = "RFC 8785, appendix B: the numbers as ECMAScript writes them")]
	[InlineData("0000000000000000", "0")]
	[InlineData("8000000000000000", "0")]
	[InlineData("0000000000000001", "5e-324")]
	[InlineData("8000000000000001", "-5e-324")]
	[InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
	[InlineData("ffefffffffffffff", "-1.7976931348623157e+308")]
	[InlineData("4340000000000000", "9007199254740992")]
	[InlineData("c340000000000000", "-9007199254740992")]
	[InlineData("4430000000000000", "295147905179352830000")]
	[InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
	[InlineData("44b52d02c7e14af6", "1e+23")]
	[InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
	[InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
	[InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
	[InlineData("444b1ae4d6e2ef50", "1e+21")]
	[InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
	[InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
	[InlineData("41b3de4355555553", "333333333.3333332")]
	[InlineData("41b3de4355555554", "333333333.33333325")]
	[InlineData("41b3de4355555555", "333333333.3333333")]
	[InlineData("41b3de4355555556", "333333333.3333334")]
	[InlineData("41b3de4355555557", "333333333.33333343")]
	[InlineData("becbf647612f3696", "-0.0000033333333333333333")]
	[InlineData("43143ff3c1cb0959", "1424953923781206.2")]
	public void RfcNumbers(string ieee, string json) => Assert.Equal(json, Jcs.Number(BitConverter.Int64BitsToDouble(Convert.ToInt64(ieee, 16))));

	[Theory(DisplayName = "RFC 8785, appendix B: NaN and Infinity have no canonical form")]
	[InlineData("7fffffffffffffff")]
	[InlineData("7ff0000000000000")]
	[InlineData("fff0000000000000")]
	public void RfcNotNumbers(string ieee) => Assert.Throws<JcsException>(() => Jcs.Number(BitConverter.Int64BitsToDouble(Convert.ToInt64(ieee, 16))));

	[Theory(DisplayName = "without canonical form: a number beyond a double, a lone surrogate in a value or in a name, a repeated property")]
	[InlineData("[1e400]")]
	[InlineData("[\"\\udead\"]")]
	[InlineData("{\"\\ud83d\": 1}")]
	[InlineData("{\"a\": {\"b\": 1, \"b\": 2}}")]
	public void NoCanonicalForm(string json)
	{
		// read without the checks of a sidecar, as JSON may come from elsewhere
		using var document = JsonDocument.Parse(json);
		Assert.Throws<JcsException>(() => Jcs.Text(document.RootElement));
	}

	[Theory(DisplayName = "a sidecar that repeats a property, or with a name that is not Unicode, is not read")]
	[InlineData("""{ "a": 1, "b": { "c": 1, "c": 2 } }""")]
	[InlineData("""{ "a": 1, "\ud83d": 2 }""")]
	public void NotRead(string json) => Assert.ThrowsAny<JsonException>(() => DocSidecar.Parse(json).Dispose());

	[Fact(DisplayName = "the canonical form leaves out the property at a pointer, and only it")]
	public void Omit()
	{
		using var document = DocSidecar.Parse("""{ "integrity": { "metadata": "m", "content": "c" }, "metadata": 1, "a/b": { "metadata": 2 } }""");
		Assert.Equal("""{"a/b":{"metadata":2},"integrity":{"content":"c"},"metadata":1}""", Jcs.Text(document.RootElement, DocIntegrity.MetadataPointer));
		Assert.Equal("""{"a/b":{},"integrity":{"content":"c","metadata":"m"},"metadata":1}""", Jcs.Text(document.RootElement, "/a~1b/metadata"));
	}

	// the hash of the metadata

	[Fact(DisplayName = "the hash of the metadata: SHA-256 of the canonical form without integrity.metadata (an independent value)")]
	public void MetadataHash()
	{
		var json = Register().ToJson();
		Assert.Equal("sha256:920e76918abc72b0922ddb11f47a82e5e8388ac14077455662fca66fa170d99a", Metadata(json));
		Assert.Equal(Metadata(json), Register().MetadataHash());
	}

	[Fact(DisplayName = "the hash of the metadata: not the indentation, the order or the hash of itself; yes the content and every value")]
	public void MetadataHashChanges()
	{
		var json = Register().ToJson();
		var hash = Metadata(json);
		using (var document = DocSidecar.Parse(json))
		{
			var reordered = "{" + string.Join(",", document.RootElement.EnumerateObject().Reverse().Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}")) + "}";
			Assert.Equal(hash, Metadata(reordered));
		}
		Assert.Equal(hash, Metadata(json.Replace("\n", "\r\n").Replace("  ", "\t")));
		Assert.Equal(hash, Metadata(json.Replace(new string('b', 64), new string('c', 64))));
		Assert.NotEqual(hash, Metadata(json.Replace(new string('a', 64), new string('c', 64))));
		Assert.NotEqual(hash, Metadata(json.Replace("\"editor\"", "\"other\"")));
		// the order of an array is part of its value
		Assert.NotEqual(hash, Metadata(json.Replace("\"backlog\",\n    \"ideas\"", "\"ideas\",\n    \"backlog\"")));
	}

	// the sidecar

	[Fact(DisplayName = "the sidecar is written in the order of the standard: tags sorted, relations by kind, two spaces, LF, UTF-8 as it is")]
	public void Write()
	{
		Assert.Equal($$"""
			{
			  "schemaVersion": "1.0",
			  "id": "0192a8f0-7c3e-7b4a-9d2e-3f4a5b6c7d8e",
			  "type": "register",
			  "profile": "backlog",
			  "status": "active",
			  "title": "Índice de ideas",
			  "tags": [
			    "backlog",
			    "ideas"
			  ],
			  "createdAt": "2026-10-18T10:00:00Z",
			  "updatedAt": "2026-10-21T10:00:00Z",
			  "createdBy": "editor",
			  "relations": [
			    {
			      "kind": "depends-on",
			      "targetId": "0192a8f1-0000-7000-8000-000000000001"
			    },
			    {
			      "kind": "related-to",
			      "targetId": "0192a8f1-0000-7000-8000-000000000002"
			    }
			  ],
			  "generation": {
			    "recipe": "docs.backlog",
			    "scope": "_docs/ideas"
			  },
			  "integrity": {
			    "content": "{{Content}}",
			    "metadata": "sha256:{{new string('b', 64)}}"
			  }
			}

			""".Replace("\r\n", "\n"), Register().ToJson());
	}

	[Fact(DisplayName = "the sidecar: what it has not, it does not write (no nulls, no empty lists)")]
	public void WriteOptional()
	{
		var adr = new DocSidecar
		{
			Id = "0192a8f0-7c3e-7b4a-9d2e-3f4a5b6c7d8e",
			Type = "adr",
			Status = "draft",
			Title = "Una decisión con \"comillas\"",
			CreatedAt = "2026-10-21T10:00:00Z",
			UpdatedAt = "2026-10-21T10:00:00Z",
			CreatedBy = "editor"
		};
		var json = adr.ToJson();
		Assert.DoesNotContain("profile", json);
		Assert.DoesNotContain("tags", json);
		Assert.DoesNotContain("relations", json);
		Assert.DoesNotContain("generation", json);
		Assert.DoesNotContain("integrity", json);
		Assert.DoesNotContain("null", json);
		Assert.Contains("\"title\": \"Una decisión con \\\"comillas\\\"\",\n", json);
	}

	[Fact(DisplayName = "the sidecar, read and written again, is the same bytes")]
	public void RoundTrip()
	{
		var json = Register().ToJson();
		using var document = DocSidecar.Parse(json);
		Assert.Empty(DocsSchemas.Validate(document.RootElement));
		Assert.Equal(json, DocSidecar.From(document.RootElement).ToJson());
	}

	[Fact(DisplayName = "sealed: valid by its schema, its hash is the one of what it writes, and sealing it again changes nothing")]
	public void Seal()
	{
		var at = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
		var markdown = "# Una decisión\n\nEl texto.\n";
		var sealedOne = new DocSidecar
		{
			Id = DocSidecar.NewId(at),
			Type = "adr",
			Status = DocTypes.Find("adr")!.InitialStatus,
			Title = "Una decisión",
			Tags = ["docs"],
			CreatedAt = DocSidecar.Instant(at),
			UpdatedAt = DocSidecar.Instant(at),
			CreatedBy = "editor"
		}.Seal(DocIntegrity.Content(markdown));
		var json = sealedOne.ToJson();
		using var document = DocSidecar.Parse(json);
		Assert.Empty(DocsSchemas.Validate(document.RootElement));
		Assert.Equal(DocIntegrity.Content(markdown), sealedOne.Integrity!.Content);
		Assert.Equal(DocIntegrity.Metadata(document.RootElement), sealedOne.Integrity.Metadata);
		Assert.Equal(json, sealedOne.Seal(sealedOne.Integrity.Content).ToJson());
		Assert.NotEqual(sealedOne.Integrity.Metadata, sealedOne.Seal(DocIntegrity.Content(markdown + "Más.\n")).Integrity!.Metadata);
	}
}
