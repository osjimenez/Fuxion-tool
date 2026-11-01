using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Self;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// A text of fx as data (plan <i>fx sobre Responses</i>, phase 9; ADR <i>Los diagnósticos de fx en las Responses</i>, 5):
/// its arguments a closed set, and its JSON for the seam, without reflection.
/// </summary>
public sealed class FxTextTest
{
	static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es");

	static FxText RoundTrip(FxText text)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
			FxTextJsonConverter.WriteText(writer, text);
		var reader = new Utf8JsonReader(buffer.WrittenSpan);
		reader.Read();
		return FxTextJsonConverter.ReadText(ref reader);
	}

	[Fact(DisplayName = "a diagnostic crosses a process as JSON: its nested texts, its numbers and a missing text with it, translated where it is written")]
	public void RoundTripOfADiagnostic()
	{
		var diagnostic = FxDiagnostic.Error(SelfUpdater.NoRelease, new(SelfUpdater.NoRelease, new FxText("self.no-release.any")));
		var message = RoundTrip(diagnostic.Message);
		Assert.Equal(diagnostic.Message, message);
		Assert.Equal(diagnostic.Message.English, message.English);
		Assert.Equal(diagnostic.Message.ToString(Spanish), message.ToString(Spanish));
		Assert.NotEqual(message.English, message.ToString(Spanish));

		var numbers = new FxText("mounts.dirty", "oss", 3, 2.5, (string?)null, (int?)null);
		var back = RoundTrip(numbers);
		Assert.Equal(numbers, back);
		Assert.Equal([true, true, false, false], new[] { back.Args[1].IsNumber, back.Args[2].IsNumber, back.Args[3].IsNumber, back.Args[4].IsNumber });
		Assert.Equal(3L, back.Args[1].Number);
		Assert.Equal(numbers.English, back.English);
	}

	[Fact(DisplayName = "its arguments: a text, a number or another text; equal by value; formatted as before")]
	public void Arguments()
	{
		Assert.Equal(new FxText("k", "a", 1, new FxText("j")), new FxText("k", "a", 1L, new FxText("j")));
		Assert.NotEqual(new FxText("k", "1"), new FxText("k", 1));
		Assert.Equal("Plain", FxText.Plain("Plain").English);
		Assert.Equal("unknown.key(a, 1)", new FxText("unknown.key", "a", 1).English);
		Assert.Equal("{text}", FxText.Plain("{text}").ToString(Spanish));
	}

	[Fact(DisplayName = "what is not a text of fx is not read as one")]
	public void NotAText()
	{
		foreach (var json in new[] { "[]", """{"args": []}""", """{"key": "k", "args": [true]}""" })
			Assert.Throws<JsonException>(() =>
			{
				var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
				reader.Read();
				return FxTextJsonConverter.ReadText(ref reader);
			});
	}
}
