using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuxion.Tool.Core.Results;

/// <summary>
/// An <see cref="FxText"/> as JSON, for the seam (a message that crosses a process and is translated where it is
/// written): <c>{"key": "…", "args": [ … ]}</c>, each argument a string, a number, null or another text. No reflection:
/// compatible with native AOT.
/// </summary>
public sealed class FxTextJsonConverter : JsonConverter<FxText>
{
	public override FxText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadText(ref reader);

	public override void Write(Utf8JsonWriter writer, FxText value, JsonSerializerOptions options) => WriteText(writer, value);

	/// <summary>Writes <paramref name="text"/>, its arguments with it.</summary>
	public static void WriteText(Utf8JsonWriter writer, FxText text)
	{
		writer.WriteStartObject();
		writer.WriteString("key", text.Key);
		writer.WriteStartArray("args");
		foreach (var arg in text.Args)
			switch (arg)
			{
				case { Nested: { } nested }:
					WriteText(writer, nested);
					break;
				case { Number: long integer }:
					writer.WriteNumberValue(integer);
					break;
				case { Number: double real }:
					writer.WriteNumberValue(real);
					break;
				case { Text: { } value }:
					writer.WriteStringValue(value);
					break;
				default:
					writer.WriteNullValue();
					break;
			}
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	/// <summary>Reads a text written by <see cref="WriteText"/>; a <see cref="JsonException"/> if it is not one.</summary>
	public static FxText ReadText(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
			throw new JsonException("A text of fx is an object.");
		string? key = null;
		var args = new List<FxArg>();
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
				throw new JsonException("A text of fx has only properties.");
			var name = reader.GetString();
			reader.Read();
			switch (name)
			{
				case "key":
					key = reader.GetString();
					break;
				case "args":
					if (reader.TokenType != JsonTokenType.StartArray)
						throw new JsonException("The arguments of a text of fx are an array.");
					while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						args.Add(reader.TokenType switch
						{
							JsonTokenType.StartObject => ReadText(ref reader),
							JsonTokenType.String => reader.GetString(),
							JsonTokenType.Number => reader.TryGetInt64(out var integer) ? (FxArg)integer : reader.GetDouble(),
							JsonTokenType.Null => (string?)null,
							_ => throw new JsonException($"An argument of a text of fx cannot be {reader.TokenType}.")
						});
					break;
				default:
					reader.Skip();
					break;
			}
		}
		return new(key ?? throw new JsonException("A text of fx has its key."), args.ToArray());
	}
}
