using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.StaticMetadata;
using Fuxion.Tools.Core.Versioning;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The static metadata: git information and the generated source (plan K, K2.1).</summary>
public sealed class StaticMetadataTest : IDisposable
{
	readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fx-test-" + Guid.NewGuid().ToString("N")[..12])).FullName;

	public void Dispose() => Directory.Delete(_dir, true);

	static FuxionToolsConfig ConfigFor(TempGitRepository repo) => new() { Versioning = new GitVersioningConfig { RepositoryPath = repo.Path } };

	// Git information

	[Fact(DisplayName = "git information: branch, commit, origin hash and first commit")]
	public void RepositoryInfo_Read()
	{
		using var repo = new TempGitRepository();
		var first = repo.Commit("c1");
		repo.Commit("c2");
		repo.Branch("feature/x");
		var head = repo.Commit("f1");
		repo.SetOrigin("https://github.com/OSJimenez/Fuxion-tool.git");

		var info = RepositoryInfoReader.TryRead(ConfigFor(repo), null);

		Assert.Equal("feature/x", info.Branch);
		Assert.Equal(head, info.Commit);
		Assert.Equal(first, info.FirstCommit);
		// normalized: no .git, lower case
		Assert.Equal(Sha256("https://github.com/osjimenez/fuxion-tool"), info.OriginUrlSHA256);
	}

	[Fact(DisplayName = "git information without origin: no hash")]
	public void RepositoryInfo_NoOrigin()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		Assert.Null(RepositoryInfoReader.TryRead(ConfigFor(repo), null).OriginUrlSHA256);
	}

	[Fact(DisplayName = "git information outside a repository: nothing, no error")]
	public void RepositoryInfo_NotARepository()
	{
		using var folder = new TempGitRepository(init: false);
		Assert.Equal(RepositoryInfo.None, RepositoryInfoReader.TryRead(ConfigFor(folder), null));
	}

	[Theory(DisplayName = "origin URL normalization")]
	[InlineData(" https://github.com/A/B.git ", "https://github.com/a/b")]
	[InlineData("https://github.com/A/B/", "https://github.com/a/b")]
	// quirk, pinned: .git is removed before the trailing slash, so it stays
	[InlineData("https://github.com/A/B.git/", "https://github.com/a/b.git")]
	[InlineData("", null)]
	[InlineData(null, null)]
	public void NormalizeOrigin(string? url, string? expected) => Assert.Equal(expected, RepositoryInfoReader.NormalizeOriginUrl(url));

	// Generated source

	[Theory(DisplayName = "class name from the project name")]
	[InlineData("Fuxion.Tools", "Fuxion_Tools")]
	[InlineData("1abc", "_1abc")]
	[InlineData("", "Project")]
	public void ClassName(string project, string expected) => Assert.Equal(expected, StaticMetadataFileGenerator.GetClassName(project));

	StaticMetadataRequest Request(VersionProps? version = null, string? path = null) => new(
		OutputPath: path ?? Path.Combine(_dir, "Fuxion.StaticMetadata.g.cs"),
		ProjectName: "Fuxion.Tools",
		Namespace: "Fx.Metadata",
		VersionProps: version,
		BuildDateUtc: "2020-01-01T00:00:00.0000000Z",
		Branch: "main",
		Commit: "abc",
		OriginUrlSHA256: "ORIGIN",
		FirstCommit: "first",
		TargetFramework: "net11.0-windows10.0.19041",
		Configuration: "Release");

	[Fact(DisplayName = "the generated source carries the request")]
	public void Generate_Content()
	{
		var request = Request(DefaultVersionPropsProvider.FromInputs(new("1.2.3", "+local")));
		StaticMetadataFileGenerator.Generate(request);
		var source = File.ReadAllText(request.OutputPath);

		Assert.Contains("// Target Framework Symbol: NET11_0", source);
		Assert.Contains("namespace Fx.Metadata.Fuxion_Tools", source);
		Assert.Contains("public static readonly string Name = \"Fuxion.Tools\";", source);
		Assert.Contains("public static readonly string TargetFramework = \"net11.0-windows10.0.19041\";", source);
		Assert.Contains("public static readonly string Configuration = \"Release\";", source);
		Assert.Contains("public static readonly string InformationalVersion = \"1.2.3+local\";", source);
		Assert.Contains("public static readonly string Branch = \"main\";", source);
		Assert.Contains("public static readonly string OriginUrlSHA256 = \"ORIGIN\";", source);
		Assert.Contains("public static readonly string FirstCommit = \"first\";", source);
	}

	[Fact(DisplayName = "disabled: the file is deleted")]
	public void DeleteIfExists()
	{
		var path = Path.Combine(_dir, "x.g.cs");
		File.WriteAllText(path, "");
		Assert.True(StaticMetadataFileGenerator.DeleteIfExists(path));
		Assert.False(StaticMetadataFileGenerator.DeleteIfExists(path));
	}

	// Two bugs that K2.1 pinned as they were (KNOWN BUG) and K3.2 fixed.

	[Fact(DisplayName = "the build date is the one of the request, so the same request does not rewrite the file")]
	public void BuildDate_FromRequest()
	{
		var request = Request(DefaultVersionPropsProvider.FromInputs(new("1.2.3", "+local")));
		Assert.True(StaticMetadataFileGenerator.Generate(request));
		Assert.Contains($"new global::System.DateTime({new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks}L, global::System.DateTimeKind.Utc)",
			File.ReadAllText(request.OutputPath));
		Thread.Sleep(20);
		Assert.False(StaticMetadataFileGenerator.Generate(request));
	}

	[Fact(DisplayName = "without version, neither the Versioning class nor its section in the Json class")]
	public void JsonWithoutVersioning()
	{
		var request = Request(version: null);
		StaticMetadataFileGenerator.Generate(request);
		var source = File.ReadAllText(request.OutputPath);
		Assert.DoesNotContain("public static partial class Versioning", source);
		Assert.DoesNotContain("{{Versioning.", source);
		Assert.Contains("{{Repository.Branch}}", source);
	}

	[Fact(DisplayName = "with version, the Versioning section of the Json class")]
	public void JsonWithVersioning()
	{
		var request = Request(DefaultVersionPropsProvider.FromInputs(new("1.2.3", "+local")));
		StaticMetadataFileGenerator.Generate(request);
		var source = File.ReadAllText(request.OutputPath).Replace("\r\n", "\n");
		Assert.Contains("\t\t\t\t\"Versioning\": {\n\t\t\t\t\t\"Version\": \"{{Versioning.Version}}\",", source);
		Assert.Contains("\t\t\t\t},\n\t\t\t\t\"Repository\": {", source);
	}

	static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
