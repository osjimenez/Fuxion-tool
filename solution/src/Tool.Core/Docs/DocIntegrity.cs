using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The integrity of a document (the standard, its metadata model, section 10): the hash of its Markdown and the hash of
/// its sidecar, each <c>sha256:</c> and 64 lower case hexadecimal digits.
/// </summary>
public static class DocIntegrity
{
	public const string Algorithm = "sha256:";

	/// <summary>The pointer of the only property the hash of the metadata leaves out: itself.</summary>
	public const string MetadataPointer = "/integrity/metadata";

	/// <summary>UTF-8 that fails on bytes that are not UTF-8, and writes no byte order mark.</summary>
	public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>
	/// The text of a Markdown as its hash sees it: read as strict UTF-8 (a <see cref="DecoderFallbackException"/> if it is
	/// not), without its byte order mark, with CRLF and CR as LF; nothing else changes, not even the last line end.
	/// </summary>
	public static string Normalize(ReadOnlySpan<byte> markdown) => Normalize(Utf8.GetString(markdown));

	/// <summary>The same normalization, for a text already read.</summary>
	public static string Normalize(string text)
		=> (text.StartsWith('﻿') ? text[1..] : text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	/// <summary>The hash of the content of a Markdown, from its bytes.</summary>
	public static string Content(ReadOnlySpan<byte> markdown) => Hash(Utf8.GetBytes(Normalize(markdown)));

	/// <summary>The hash of the content of a Markdown, from its text.</summary>
	public static string Content(string markdown) => Hash(Utf8.GetBytes(Normalize(markdown)));

	/// <summary>The hash of the metadata of a sidecar: its canonical form (RFC 8785) without <c>integrity.metadata</c>.</summary>
	public static string Metadata(JsonElement sidecar) => Hash(Jcs.Canonicalize(sidecar, MetadataPointer));

	public static string Hash(ReadOnlySpan<byte> bytes) => Algorithm + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
