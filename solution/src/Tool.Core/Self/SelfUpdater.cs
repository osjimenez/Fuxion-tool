using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Self;

/// <summary>
/// What <c>fx self update</c> did: the version of fx, the release it found (none: it did not look for one), whether it
/// installed it and, for an installation that fx does not update itself (dotnet, dnx), the command that does.
/// </summary>
public sealed record SelfUpdateResult(FxInstall Install, string Current, string? Release, bool Updated, string? Command, IReadOnlyList<FxDiagnostic> Diagnostics);

/// <summary>
/// <c>fx self update</c> (plan S): fx installed from a release updates itself from another one, as the install scripts
/// do (the archive of its platform, checked against <c>SHA256SUMS</c>); a tool of dotnet and dnx are updated with
/// dotnet, which cannot replace the executable that runs on Windows, so fx says the command.
/// </summary>
public static class SelfUpdater
{
	/// <summary>The repository whose releases have fx (tag <c>release/&lt;version&gt;</c>).</summary>
	public const string Repository = "osjimenez/Fuxion-tool";

	public const string LocalBuild = "self.local";
	public const string NotFx = "self.not-fx";
	public const string Platform = "self.platform";
	public const string NoRelease = "self.no-release";
	public const string Network = "self.network";
	public const string NoArchive = "self.no-archive";
	public const string Checksum = "self.checksum";
	public const string ReplaceFailed = "self.replace-failed";

	/// <summary>What there is no release of, when there is none at all.</summary>
	const string Latest = "self.no-release.any";

	/// <summary>Where the executable that ran goes on Windows while the new one takes its place.</summary>
	const string OldSuffix = ".old";

	/// <param name="current">The informational version of the fx that runs.</param>
	/// <param name="version">The version to install; the latest release, without it.</param>
	/// <param name="check">Only find the release, change nothing.</param>
	/// <param name="downloading">Told the version and the platform before the download.</param>
	public static SelfUpdateResult Run(FxInstall install, string current, string? version, bool check, HttpClient http, Action<string, string>? downloading = null,
		CancellationToken cancellationToken = default)
	{
		var diagnostics = new List<FxDiagnostic>();
		SelfUpdateResult Result(string? release = null, bool updated = false, string? command = null)
			=> new(install, FxToolVersion.Core(current), release, updated, command, diagnostics);
		switch (install.Kind)
		{
			case FxInstallKind.DotnetTool or FxInstallKind.Package:
				return Result(command: install.Command(version));
			case FxInstallKind.Local:
				diagnostics.Add(FxDiagnostic.Warning(LocalBuild, new(LocalBuild), install.Executable));
				return Result();
			case FxInstallKind.Unknown:
				diagnostics.Add(FxDiagnostic.Error(NotFx, new(NotFx), install.Executable));
				return Result();
		}
		if (Rid() is not { } rid)
		{
			diagnostics.Add(Error(Platform, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture));
			return Result();
		}
		if (Find(http, version, current, diagnostics, cancellationToken) is not { } found)
			return Result();
		var (release, assets) = found;

		// the latest, if newer than this one; the one asked for, if another
		var order = FxToolVersion.Compare(release, current);
		if (order is null || (version is null ? order <= 0 : order == 0) || check)
			return Result(release);
		var name = ArchiveName(release, rid);
		if (!assets.TryGetValue(name, out var archiveUrl) || !assets.TryGetValue("SHA256SUMS", out var sumsUrl))
		{
			diagnostics.Add(Error(NoArchive, $"release/{release}", assets.ContainsKey(name) ? "SHA256SUMS" : name));
			return Result(release);
		}

		// in the folder of fx, so that moving is renaming
		var folder = Path.GetDirectoryName(install.Executable)!;
		var temp = Path.Combine(folder, $".fx-update-{Guid.NewGuid():N}");
		var url = archiveUrl;
		try
		{
			Directory.CreateDirectory(temp);
			downloading?.Invoke(release, rid);
			var archive = Path.Combine(temp, name);
			Download(http, archiveUrl, archive, current, cancellationToken);
			url = sumsUrl;
			var sums = Path.Combine(temp, "SHA256SUMS");
			Download(http, sumsUrl, sums, current, cancellationToken);
			if (ExpectedHash(File.ReadAllText(sums), name) is not { } expected || expected != Hash(archive))
			{
				diagnostics.Add(Error(Checksum, name));
				return Result(release);
			}
			var extracted = Path.Combine(temp, "fx");
			Extract(archive, extracted);
			var executable = Path.Combine(extracted, ExecutableName(rid));
			if (!File.Exists(executable))
			{
				diagnostics.Add(Error(NoArchive, name, ExecutableName(rid)));
				return Result(release);
			}
			// the rest of the archive (its license), next to it
			foreach (var file in Directory.EnumerateFiles(extracted).Where(f => f != executable))
				File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
			Replace(executable, install.Executable);
			return Result(release, updated: true);
		}
		catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
		{
			diagnostics.Add(Error(Network, url, ex.Message));
			return Result(release);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(FxDiagnostic.Error(ReplaceFailed, new(ReplaceFailed, ex.Message), install.Executable));
			return Result(release);
		}
		finally
		{
			try
			{
				if (Directory.Exists(temp))
					Directory.Delete(temp, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// a folder left behind is not worth failing for
			}
		}
	}

	/// <summary>
	/// What the update of fx left on Windows: the executable it replaced, which could not go while it ran. fx deletes it
	/// when it starts.
	/// </summary>
	public static void CleanUp(string? executable)
	{
		if (!OperatingSystem.IsWindows() || executable is null || !File.Exists(executable + OldSuffix))
			return;
		try
		{
			File.Delete(executable + OldSuffix);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// another fx still runs it: the next one
		}
	}

	/// <summary>The platform of the system (not of the process: an x64 fx on ARM64 gets the native one), as the releases name it; null if fx has no build for it.</summary>
	public static string? Rid()
	{
		var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
		var architecture = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => null
		};
		return os is null || architecture is null ? null : $"{os}-{architecture}";
	}

	/// <summary>The archive of a release for a platform: a zip on Windows, a tar.gz elsewhere (with the permission to run fx).</summary>
	public static string ArchiveName(string version, string rid) => $"fx-{version}-{rid}{(IsWindows(rid) ? ".zip" : ".tar.gz")}";

	public static string ExecutableName(string rid) => IsWindows(rid) ? "fx.exe" : "fx";

	static bool IsWindows(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

	/// <summary>The hash <c>SHA256SUMS</c> gives <paramref name="name"/> (a line <c>&lt;hash&gt;  &lt;file&gt;</c>, the file maybe after <c>*</c>); null if none.</summary>
	public static string? ExpectedHash(string sums, string name)
	{
		foreach (var line in sums.Split('\n').Select(l => l.Trim()))
		{
			var space = line.IndexOfAny([' ', '\t']);
			if (space > 0 && line[space..].Trim().TrimStart('*') == name)
				return line[..space].ToLowerInvariant();
		}
		return null;
	}

	/// <summary>
	/// Puts <paramref name="file"/> in the place of <paramref name="executable"/>. On Windows the executable that runs
	/// cannot be written nor deleted, but it can be renamed: it goes aside, and the next fx deletes it
	/// (<see cref="CleanUp"/>); elsewhere a rename over it is enough, the one that runs keeps its file.
	/// </summary>
	public static void Replace(string file, string executable)
	{
		if (OperatingSystem.IsWindows())
		{
			var old = executable + OldSuffix;
			if (File.Exists(old))
				File.Delete(old);
			File.Move(executable, old);
			try
			{
				File.Move(file, executable);
			}
			catch
			{
				File.Move(old, executable);
				throw;
			}
		}
		else
		{
			File.SetUnixFileMode(file, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
			File.Move(file, executable, overwrite: true);
		}
	}

	/// <summary>The release of <paramref name="version"/> (its tag is <c>release/&lt;version&gt;</c>), or the latest: its version and its assets by name.</summary>
	static (string Version, IReadOnlyDictionary<string, string> Assets)? Find(HttpClient http, string? version, string current, List<FxDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var url = $"https://api.github.com/repos/{Repository}/releases/" + (version is null ? "latest" : $"tags/release/{version}");
		try
		{
			using var request = Request(url, "application/vnd.github+json", current);
			using var response = http.Send(request, cancellationToken);
			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				diagnostics.Add(Error(NoRelease, version is null ? (object)new FxText(Latest) : $"release/{version}"));
				return null;
			}
			response.EnsureSuccessStatusCode();
			using var json = JsonDocument.Parse(response.Content.ReadAsStream(cancellationToken));
			var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
			var assets = json.RootElement.GetProperty("assets").EnumerateArray()
				.ToDictionary(a => a.GetProperty("name").GetString() ?? "", a => a.GetProperty("browser_download_url").GetString() ?? "");
			return (tag.StartsWith("release/", StringComparison.Ordinal) ? tag["release/".Length..] : tag, assets);
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
			                           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
		{
			diagnostics.Add(Error(Network, url, ex.Message));
			return null;
		}
	}

	static void Download(HttpClient http, string url, string file, string current, CancellationToken cancellationToken)
	{
		using var request = Request(url, "application/octet-stream", current);
		using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		using var source = response.Content.ReadAsStream(cancellationToken);
		using var target = File.Create(file);
		source.CopyTo(target);
	}

	/// <summary>A request as GitHub wants it: with who asks (fx and its version).</summary>
	static HttpRequestMessage Request(string url, string accept, string current)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("fx", FxToolVersion.Core(current)));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
		return request;
	}

	static string Hash(string file)
	{
		using var stream = File.OpenRead(file);
		return Convert.ToHexStringLower(SHA256.HashData(stream));
	}

	static void Extract(string archive, string folder)
	{
		if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			ZipFile.ExtractToDirectory(archive, folder);
			return;
		}
		Directory.CreateDirectory(folder);
		using var file = File.OpenRead(archive);
		using var gzip = new GZipStream(file, CompressionMode.Decompress);
		TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: true);
	}

	static FxDiagnostic Error(string code, params object?[] args) => FxDiagnostic.Error(code, new FxText(code, args));
}
