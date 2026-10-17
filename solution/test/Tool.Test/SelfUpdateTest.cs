using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Self;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary><c>fx self update</c> (plan S): how fx was installed, and fx updated from a release (of a GitHub in memory).</summary>
public sealed class SelfUpdateTest
{
	static readonly string Home = Path.Combine(TempGitRepository.TempRoot, "home");

	static string In(params string[] parts) => Path.Combine([Home, ..parts]);

	[Fact(DisplayName = "installed with dotnet tool: global or in a folder; dotnet updates it, with the command fx says")]
	public void Detect_DotnetTool()
	{
		var global = FxInstall.Detect(In(".dotnet", "tools", ".store", "fuxion.tool", "0.7.4", "fuxion.tool.win-x64", "0.7.4", "tools", "any", "win-x64", "fx.exe"), "0.7.4+abc", Home);
		Assert.Equal((FxInstallKind.DotnetTool, true, "dotnet-tool"), (global.Kind, global.Global, global.Name));
		Assert.Equal("dotnet tool update -g Fuxion.Tool", global.Command(null));
		Assert.Equal("dotnet tool update -g Fuxion.Tool --version 0.7.6", global.Command("0.7.6"));

		var folder = FxInstall.Detect(In("tools", ".store", "fuxion.tool", "0.7.4", "fuxion.tool.linux-x64", "0.7.4", "tools", "any", "linux-x64", "fx"), "0.7.4+abc", Home);
		Assert.Equal((FxInstallKind.DotnetTool, false, In("tools")), (folder.Kind, folder.Global, folder.ToolPath));
		Assert.Equal($"dotnet tool update --tool-path \"{In("tools")}\" Fuxion.Tool", folder.Command(null));
	}

	[Fact(DisplayName = "run with dnx, from the cache of NuGet: dnx runs the version it is given")]
	public void Detect_Package()
	{
		var package = FxInstall.Detect(In(".nuget", "packages", "fuxion.tool.win-x64", "0.7.4", "tools", "any", "win-x64", "fx.exe"), "0.7.4+abc", Home);
		Assert.Equal(FxInstallKind.Package, package.Kind);
		Assert.Equal("dnx Fuxion.Tool@0.7.6", package.Command("0.7.6"));
		Assert.Equal("dnx Fuxion.Tool", package.Command(null));
	}

	[Fact(DisplayName = "from a release, fx itself; built from its sources, or not fx, nothing")]
	public void Detect_Release()
	{
		Assert.Equal(FxInstallKind.Release, FxInstall.Detect(In("AppData", "Local", "fx", "bin", "fx.exe"), "0.7.4+62d65aa", Home).Kind);
		Assert.Equal(FxInstallKind.Release, FxInstall.Detect(In(".local", "bin", "fx"), "0.7.4+62d65aa", Home).Kind);
		Assert.Equal(FxInstallKind.Local, FxInstall.Detect(In("repo", "bin", "fx.exe"), "0.7.6+local.62d65aa", Home).Kind);
		Assert.Equal(FxInstallKind.Unknown, FxInstall.Detect(In("bin", "Fuxion.Tool.Test.exe"), "0.7.4+62d65aa", Home).Kind);
		Assert.Null(FxInstall.Detect(In("bin", "fx"), "0.7.4+62d65aa", Home).Command(null));
	}

	[Fact(DisplayName = "the hash of SHA256SUMS, by its file (with or without *)")]
	public void ExpectedHash()
	{
		const string sums = "ABC123  fx-1.0.0-win-x64.zip\ndef456 *fx-1.0.0-linux-x64.tar.gz\n";
		Assert.Equal("abc123", SelfUpdater.ExpectedHash(sums, "fx-1.0.0-win-x64.zip"));
		Assert.Equal("def456", SelfUpdater.ExpectedHash(sums, "fx-1.0.0-linux-x64.tar.gz"));
		Assert.Null(SelfUpdater.ExpectedHash(sums, "fx-1.0.0-osx-arm64.tar.gz"));
	}

	/// <summary>fx installed in a folder of its own, with its license: what a release leaves.</summary>
	sealed class Installed : IDisposable
	{
		public readonly string Folder = TempGitRepository.TempFolder("fx-installed-");
		public readonly string Rid = SelfUpdater.Rid()!;

		public Installed()
		{
			File.WriteAllText(Executable, "old");
			File.WriteAllText(Path.Combine(Folder, "LICENSE"), "license old");
		}

		public string Executable => Path.Combine(Folder, SelfUpdater.ExecutableName(Rid));

		public FxInstall Install => new(FxInstallKind.Release, Executable);

		public string Read(string name = "") => File.ReadAllText(name.Length == 0 ? Executable : Path.Combine(Folder, name));

		public void Dispose() => Directory.Delete(Folder, recursive: true);
	}

	/// <summary>The releases of GitHub, of one version, in memory: the API, its archive and its SHA256SUMS.</summary>
	sealed class GitHub : HttpMessageHandler
	{
		readonly string _version;
		readonly string _name;
		readonly byte[] _archive;
		readonly string _sums;

		public GitHub(string version, string rid, string content, string? sums = null)
		{
			_version = version;
			_name = SelfUpdater.ArchiveName(version, rid);
			_archive = Archive(rid, content);
			_sums = sums ?? $"{Convert.ToHexStringLower(SHA256.HashData(_archive))}  {_name}\n";
		}

		public List<string> Requests { get; } = [];

		/// <summary>The archive of a release: fx and its license.</summary>
		static byte[] Archive(string rid, string content)
		{
			var folder = TempGitRepository.TempFolder("fx-archive-");
			try
			{
				var executable = Path.Combine(folder, SelfUpdater.ExecutableName(rid));
				File.WriteAllText(executable, content);
				File.WriteAllText(Path.Combine(folder, "LICENSE"), $"license {content}");
				var buffer = new MemoryStream();
				if (OperatingSystem.IsWindows())
					ZipFile.CreateFromDirectory(folder, buffer);
				else
				{
					File.SetUnixFileMode(executable, (UnixFileMode)0b111_101_101);
					using var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true);
					TarFile.CreateFromDirectory(folder, gzip, includeBaseDirectory: false);
				}
				return buffer.ToArray();
			}
			finally
			{
				Directory.Delete(folder, recursive: true);
			}
		}

		protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var url = request.RequestUri!.ToString();
			Requests.Add(url);
			Assert.StartsWith("fx/", request.Headers.UserAgent.ToString());
			const string api = $"https://api.github.com/repos/{SelfUpdater.Repository}/releases/";
			if (url == $"{api}latest" || url == $"{api}tags/release/{_version}")
				return Content(Encoding.UTF8.GetBytes($$"""
					{"tag_name": "release/{{_version}}", "assets": [
					  {"name": "{{_name}}", "browser_download_url": "https://example.test/{{_name}}"},
					  {"name": "SHA256SUMS", "browser_download_url": "https://example.test/SHA256SUMS"}]}
					"""));
			if (url == $"https://example.test/{_name}")
				return Content(_archive);
			if (url == "https://example.test/SHA256SUMS")
				return Content(Encoding.UTF8.GetBytes(_sums));
			return new(HttpStatusCode.NotFound);
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

		static HttpResponseMessage Content(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
	}

	static SelfUpdateResult Run(FxInstall install, string current, GitHub github, string? version = null, bool check = false)
	{
		using var http = new HttpClient(github);
		return SelfUpdater.Run(install, current, version, check, http, cancellationToken: TestContext.Current.CancellationToken);
	}

	[Fact(DisplayName = "from a release: the archive of its platform, checked, replaces fx and its license; on Windows the old one waits aside until fx starts again")]
	public void Update()
	{
		using var fx = new Installed();
		var github = new GitHub("9.9.9", fx.Rid, "new");
		var result = Run(fx.Install, "1.0.0+abc", github);
		Assert.Empty(result.Diagnostics);
		Assert.Equal((true, "9.9.9", "1.0.0"), (result.Updated, result.Release, result.Current));
		Assert.Equal(("new", "license new"), (fx.Read(), fx.Read("LICENSE")));
		Assert.Equal(3, github.Requests.Count);
		// nothing of the download stays
		Assert.Empty(Directory.EnumerateDirectories(fx.Folder));
		if (OperatingSystem.IsWindows())
		{
			Assert.Equal("old", fx.Read("fx.exe.old"));
			SelfUpdater.CleanUp(fx.Executable);
			Assert.False(File.Exists(fx.Executable + ".old"));
		}
		else
			Assert.True(File.GetUnixFileMode(fx.Executable).HasFlag(UnixFileMode.UserExecute));
	}

	[Fact(DisplayName = "--check says the release and changes nothing; the latest, if not newer, is not installed; one asked for, older too")]
	public void Update_CheckAndVersions()
	{
		using var fx = new Installed();
		var github = new GitHub("9.9.9", fx.Rid, "new");
		var check = Run(fx.Install, "1.0.0+abc", github, check: true);
		Assert.Equal((false, "9.9.9"), (check.Updated, check.Release));
		Assert.False(Run(fx.Install, "9.9.9+abc", github).Updated);
		Assert.False(Run(fx.Install, "10.0.0+abc", github).Updated);
		Assert.False(Run(fx.Install, "9.9.9+abc", github, version: "9.9.9").Updated);
		// only the API was asked, the archive was not downloaded
		Assert.All(github.Requests, r => Assert.StartsWith("https://api.github.com/", r));
		Assert.Equal("old", fx.Read());

		var older = Run(fx.Install, "10.0.0+abc", github, version: "9.9.9");
		Assert.True(older.Updated);
		Assert.Equal("new", fx.Read());
	}

	[Fact(DisplayName = "an archive that is not the one of SHA256SUMS, or a release that does not exist: an error, and fx does not change")]
	public void Update_Errors()
	{
		using var fx = new Installed();
		var wrong = Run(fx.Install, "1.0.0+abc", new GitHub("9.9.9", fx.Rid, "new", sums: $"0000  {SelfUpdater.ArchiveName("9.9.9", fx.Rid)}\n"));
		Assert.Equal((false, SelfUpdater.Checksum), (wrong.Updated, Assert.Single(wrong.Diagnostics).Code));
		Assert.Equal("old", fx.Read());
		Assert.Empty(Directory.EnumerateDirectories(fx.Folder));

		var missing = Run(fx.Install, "1.0.0+abc", new GitHub("9.9.9", fx.Rid, "new"), version: "1.2.3");
		var error = Assert.Single(missing.Diagnostics);
		Assert.Equal((FxSeverity.Error, SelfUpdater.NoRelease), (error.Severity, error.Code));
		Assert.Contains("release/1.2.3", error.Message.English);
	}

	[Fact(DisplayName = "with dotnet, dnx, built from its sources or not fx: nothing is asked of GitHub")]
	public void Update_NotFromRelease()
	{
		var github = new GitHub("9.9.9", SelfUpdater.Rid()!, "new");
		var tool = Run(FxInstall.Detect(In(".dotnet", "tools", ".store", "fuxion.tool", "1.0.0", "x", "fx"), "1.0.0+abc", Home), "1.0.0+abc", github, version: "9.9.9");
		Assert.Equal(("dotnet tool update -g Fuxion.Tool --version 9.9.9", false), (tool.Command, tool.Updated));
		Assert.Empty(tool.Diagnostics);
		var local = Run(new FxInstall(FxInstallKind.Local, In("fx")), "1.0.0+local.abc", github);
		var l = Assert.Single(local.Diagnostics);
		Assert.Equal((FxSeverity.Warning, SelfUpdater.LocalBuild), (l.Severity, l.Code));
		var unknown = Run(new FxInstall(FxInstallKind.Unknown, In("test.exe")), "1.0.0+abc", github);
		var u = Assert.Single(unknown.Diagnostics);
		Assert.Equal((FxSeverity.Error, SelfUpdater.NotFx), (u.Severity, u.Code));
		Assert.Empty(github.Requests);
	}
}

/// <summary><c>fx self update</c> from the command line: in the tests, fx runs in process, so it is not an installed fx.</summary>
[Collection(nameof(ProcessCollection))]
public sealed class SelfUpdateCommandTest
{
	[Fact(DisplayName = "fx self update --output json: how fx was installed, and nothing done when it is not an installed fx")]
	public void Json()
	{
		var stdout = new StringWriter();
		InProcess.Run(["self", "update", "--output", "json"], stdout, new StringWriter(), TempGitRepository.TempRoot);
		var document = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-self-update/1", document.GetProperty("schema").GetString());
		Assert.Contains(document.GetProperty("install").GetString(), new[] { "local", "unknown" });
		Assert.False(document.GetProperty("updated").GetBoolean());
		Assert.Contains(document.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()), c => c is SelfUpdater.LocalBuild or SelfUpdater.NotFx);
	}
}
