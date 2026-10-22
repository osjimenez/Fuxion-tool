using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A key for an entry: the one reserved, or a provisional one (<c>I-~3f9a</c>) when the provider could not reserve it.</summary>
public sealed record DocKey(string Key, bool Provisional, FxText? Problem = null);

/// <summary>
/// The short keys of the entries of a register (the SPEC of the module, 5.7; the ADR "claves con provider opcional"):
/// only for the registers of <c>docs.yaml</c> that declare a prefix and a provider.
/// <list type="bullet">
/// <item><c>repo</c>: the next after the highest of the register, here and in every branch git has (local, and remote
/// already fetched). Without network: two people offline can take the same one, and the check sees it when they join.</item>
/// <item><c>git</c>: a reservation on the remote of the space, atomic: the push of <c>refs/fx/keys/&lt;key&gt;</c>, only if
/// it does not exist (the first wins; the next one takes the following). Its commit says who and what for; it is in no
/// branch. Without network, a provisional key, which <c>fx doc key --confirm</c> reserves later.</item>
/// </list>
/// </summary>
public static class DocKeys
{
	public const string NotKeyed = "docs.not-keyed";
	public const string KeyFailed = "docs.key-failed";
	public const string ProvisionalKey = "docs.provisional-key";
	public const string InvalidKey = "docs.invalid-key";

	/// <summary>Where the reservations live in git, one reference per key.</summary>
	public const string References = "refs/fx/keys/";

	const string Remote = "origin";

	/// <summary>The register of <c>docs.yaml</c> that <paramref name="register"/> is, if it uses keys.</summary>
	public static DocsKeyedRegister? Keyed(DocCatalog catalog, DocFile register)
		=> catalog.Config.Registers.FirstOrDefault(r => string.Equals(r.Path, register.Path, StringComparison.Ordinal));

	/// <summary>Whether <paramref name="key"/> is a provisional one, still to reserve.</summary>
	public static bool IsProvisional(string key) => key.Contains("-~", StringComparison.Ordinal);

	static long? Number(string key, string prefix)
		=> key.StartsWith($"{prefix}-", StringComparison.Ordinal) && long.TryParse(key[(prefix.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

	/// <summary>The keys of the entries of a sidecar, read loosely: also from one that is not valid.</summary>
	static IEnumerable<string> Keys(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
				return [];
			return entries.EnumerateArray()
				.Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
				.Select(e => e.GetProperty("key").GetString()!)
				.ToList();
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>
	/// The numbers of <paramref name="prefix"/> taken: in <paramref name="current"/> (the keys of the register as it is),
	/// in its sidecar in every branch git has, and in the reservations git has fetched.
	/// </summary>
	public static HashSet<long> Taken(DocCatalog catalog, DocFile register, string prefix, IEnumerable<string> current)
	{
		var taken = current.Select(k => Number(k, prefix)).OfType<long>().ToHashSet();
		if (register.Sidecar is null || GitClient.Discover(catalog.Root) is not { } git)
			return taken;
		var path = Path.GetRelativePath(git.Root, register.Sidecar).Replace('\\', '/');
		var refs = git.Execute("for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes", References);
		foreach (var reference in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (reference.StartsWith(References, StringComparison.Ordinal))
			{
				if (Number(reference[References.Length..], prefix) is { } reserved)
					taken.Add(reserved);
			}
			else if (git.Execute("show", $"{reference}:{path}") is { ExitCode: 0 } show)
				taken.UnionWith(Keys(show.StandardOutput).Select(k => Number(k, prefix)).OfType<long>());
		return taken;
	}

	static string Next(string prefix, HashSet<long> taken) => $"{prefix}-{(taken.Count == 0 ? 1 : taken.Max() + 1).ToString(CultureInfo.InvariantCulture)}";

	/// <summary>A provisional key: the prefix, and four hexadecimal digits at random.</summary>
	static string Provisional(string prefix) => $"{prefix}-~{RandomNumberGenerator.GetHexString(4, lowercase: true)}";

	/// <summary>
	/// The key for a new entry of <paramref name="register"/>, by its provider. With <paramref name="reserve"/> false (a dry
	/// run), the one it would get, reserving nothing.
	/// </summary>
	public static DocKey Reserve(DocCatalog catalog, DocFile register, DocsKeyedRegister keyed, IEnumerable<string> current, string actor, string? title, bool reserve)
	{
		var taken = Taken(catalog, register, keyed.Prefix, current);
		if (keyed.Provider != "git")
			return new(Next(keyed.Prefix, taken), false);
		if (GitClient.Discover(catalog.Root) is not { } git || git.RemoteUrl(Remote) is null)
			return new(Provisional(keyed.Prefix), true, new(KeyFailed, keyed.Path, new FxText($"{KeyFailed}.no-remote", Remote)));

		// what the remote has reserved; without network, a provisional key
		var remote = git.Execute("ls-remote", Remote, $"{References}*");
		if (remote.ExitCode != 0)
			return new(Provisional(keyed.Prefix), true, new(KeyFailed, keyed.Path, FxText.Plain(remote.StandardError.Trim())));
		foreach (var line in remote.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			if (line.Split('\t') is [_, var name] && name.Trim().StartsWith(References, StringComparison.Ordinal) && Number(name.Trim()[References.Length..], keyed.Prefix) is { } n)
				taken.Add(n);
		if (!reserve)
			return new(Next(keyed.Prefix, taken), false);

		// the reservation: a commit of an empty tree, pushed only if its reference does not exist; the first wins
		var empty = Path.GetTempFileName();
		string tree;
		try
		{
			tree = git.Execute("hash-object", "-t", "tree", "-w", empty).StandardOutput.Trim();
		}
		finally
		{
			File.Delete(empty);
		}
		for (var attempt = 0; attempt < 50; attempt++)
		{
			var key = Next(keyed.Prefix, taken);
			var commit = git.Execute("-c", $"user.name={actor}", "-c", $"user.email={actor}@fx.invalid", "commit-tree", tree, "-m",
				$"fx: {key}, of {keyed.Path}{(title is null ? "" : $": {title}")}");
			if (commit.ExitCode != 0)
				return new(Provisional(keyed.Prefix), true, new(KeyFailed, keyed.Path, FxText.Plain(commit.StandardError.Trim())));
			var push = git.Execute("push", "--quiet", Remote, $"{commit.StandardOutput.Trim()}:{References}{key}", $"--force-with-lease={References}{key}:");
			if (push.ExitCode == 0)
			{
				git.Execute("update-ref", $"{References}{key}", commit.StandardOutput.Trim());
				return new(key, false);
			}
			// taken by someone else: the next one; anything else (the network), a provisional key
			if (!(push.StandardError.Contains("stale info", StringComparison.Ordinal) || push.StandardError.Contains("already exists", StringComparison.Ordinal)
			      || push.StandardError.Contains("rejected", StringComparison.Ordinal)))
				return new(Provisional(keyed.Prefix), true, new(KeyFailed, keyed.Path, FxText.Plain(push.StandardError.Trim())));
			taken.Add(Number(key, keyed.Prefix)!.Value);
		}
		return new(Provisional(keyed.Prefix), true, new(KeyFailed, keyed.Path, new FxText($"{KeyFailed}.attempts")));
	}
}
