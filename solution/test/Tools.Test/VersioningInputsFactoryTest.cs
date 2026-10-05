using System;
using System.IO;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Versioning;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>
/// Characterization of the git versioning scheme (design §9) as Fuxion-plus used it when the tool was extracted
/// (plan K, K2.1). The implementation moves from LibGit2Sharp to the git executable in K2.2: these tests must keep
/// passing without changes.
/// </summary>
public sealed class VersioningInputsFactoryTest
{
	static VersioningInputs FromGit(TempGitRepository repo, bool failOnError = true, bool forceCi = false, string? suffix = null)
		=> VersioningInputsFactory.Create(new()
		{
			Versioning = new GitVersioningConfig
			{
				RepositoryPath = repo.Path,
				FailOnError = failOnError,
				ForceCIEnvironment = forceCi,
				InformationalSuffix = suffix
			}
		});

	/// <summary>main with <c>version/1.2.0</c> on the first commit.</summary>
	static TempGitRepository TaggedMain(string tag = "version/1.2.0")
	{
		var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag(tag);
		return repo;
	}

	// Stable branches

	[Fact(DisplayName = "main: patch = commits since the tag")]
	public void Main_CountsCommitsSinceTheTag()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Commit("c3");
		Assert.Equal(new("1.2.2", "+local"), FromGit(repo));
	}

	[Fact(DisplayName = "main: on the tagged commit, patch 0")]
	public void Main_OnTheTaggedCommit()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		Assert.Equal("1.2.0", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "master is a stable branch too")]
	public void Master_IsStable()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Git("branch", "-m", "master");
		repo.Commit("c2");
		Assert.Equal("1.2.1", FromGit(repo).BaseVersion);
	}

	// develop, feature, release, preview, other

	[Fact(DisplayName = "develop: -alpha, patch = commits since the tag")]
	public void Develop_Alpha()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("develop");
		repo.Commit("d1");
		repo.Commit("d2");
		Assert.Equal("1.2.3-alpha", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "feature: patch of the merge base, then commits on the branch")]
	public void Feature_WithoutMerges()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("feature/My_Thing");
		repo.Commit("f1");
		repo.Commit("f2");
		Assert.Equal("1.2.1-feature.my-thing.2", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "feature: right after branching, 0 commits")]
	public void Feature_RightAfterBranching()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("feature/x");
		Assert.Equal("1.2.1-feature.x.0", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "feature: nested name, segments joined with dots and sanitized")]
	public void Feature_NestedName()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("feature/team/New.Api");
		repo.Commit("f1");
		Assert.Equal("1.2.1-feature.team.new-api.1", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "feature: after merging main, base from main and commits since that merge")]
	public void Feature_AfterMergingMain()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("feature/x");
		repo.Commit("f1");
		repo.Checkout("main");
		repo.Commit("c3");
		repo.Commit("c4");
		repo.Checkout("feature/x");
		repo.Merge("main");
		repo.Commit("f2");
		// merge base = c4 (3 commits after the tag); the merge commit and f2 since the last merge of main
		Assert.Equal("1.2.3-feature.x.2", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "release: tag version and -rc.{name}.{commits}")]
	public void Release_Rc()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Branch("release/2.0");
		repo.Commit("r1");
		repo.Commit("r2");
		Assert.Equal("1.2.0-rc.2-0.3", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "preview: tag version and -preview.{name}.{commits}")]
	public void Preview()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Branch("preview/net12");
		repo.Commit("p1");
		Assert.Equal("1.2.0-preview.net12.1", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "any other branch: -{branch}, sanitized")]
	public void OtherBranch()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Branch("Hotfix_Login");
		repo.Commit("h1");
		repo.Commit("h2");
		Assert.Equal("1.2.2-hotfix-login", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "detached HEAD: treated as a branch named '(no branch)'")]
	public void DetachedHead()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		var c2 = repo.Commit("c2");
		repo.Commit("c3");
		repo.Checkout(c2);
		Assert.Equal("1.2.1-no-branch", FromGit(repo).BaseVersion);
	}

	// Tags

	[Fact(DisplayName = "the nearest tag in the history wins")]
	public void NewerTagInTheHistory()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain("version/1.0.0");
		repo.Commit("c2");
		repo.Commit("c3");
		repo.Tag("version/1.1.0");
		repo.Commit("c4");
		Assert.Equal("1.1.1", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "two tags on the same commit: the highest wins")]
	public void TwoTagsOnTheSameCommit()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Tag("version/1.10.0");
		repo.Commit("c2");
		Assert.Equal("1.10.1", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "annotated tags count as well")]
	public void AnnotatedTag()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/1.2.0", annotated: true);
		repo.Commit("c2");
		Assert.Equal("1.2.1", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "tags without the prefix or that are not a version are ignored")]
	public void OtherTagsIgnored()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		repo.Tag("v9.0.0");
		repo.Tag("version/next");
		repo.Commit("c3");
		Assert.Equal("1.2.2", FromGit(repo).BaseVersion);
	}

	[Fact(DisplayName = "a version tag with patch other than 0 fails, even without failOnError")]
	public void TagWithPatch_Fails()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain("version/1.2.3");
		var ex = Assert.Throws<InvalidOperationException>(() => FromGit(repo, failOnError: false));
		Assert.Equal("Git versioning failed: tag 'version/1.2.3' patch must be 0.", ex.Message);
	}

	// Errors

	[Fact(DisplayName = "no version tag: fails with failOnError")]
	public void NoTag_Fails()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		var ex = Assert.Throws<InvalidOperationException>(() => FromGit(repo));
		Assert.StartsWith("Git versioning failed: No version tag found for stable branch in repo '", ex.Message);
	}

	[Fact(DisplayName = "no version tag: 0.1.0 without failOnError")]
	public void NoTag_FallsBack()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		Assert.Equal(new("0.1.0", "+local"), FromGit(repo, failOnError: false));
	}

	[Fact(DisplayName = "repository without commits: fails")]
	public void EmptyRepository_Fails()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		var ex = Assert.Throws<InvalidOperationException>(() => FromGit(repo));
		Assert.Equal("Git versioning failed: Git HEAD not available (repository has no commits).", ex.Message);
	}

	[Fact(DisplayName = "not a repository: fails")]
	public void NotARepository_Fails()
	{
		using var ci = CiEnvironment.Local();
		using var folder = new TempGitRepository(init: false);
		var ex = Assert.Throws<InvalidOperationException>(() => FromGit(folder));
		Assert.Equal("Git versioning failed: Git repository not found (Repository.Discover returned null).", ex.Message);
	}

	// Repository path

	[Fact(DisplayName = "the repository is found from a subfolder")]
	public void FoundFromSubfolder()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		var sub = Directory.CreateDirectory(Path.Combine(repo.Path, "solution", "src")).FullName;
		var inputs = VersioningInputsFactory.Create(new() { Versioning = new GitVersioningConfig { RepositoryPath = sub, FailOnError = true } });
		Assert.Equal("1.2.1", inputs.BaseVersion);
	}

	[Fact(DisplayName = "a relative repositoryPath is relative to the config file")]
	public void RelativeToConfigFile()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		repo.Commit("c2");
		var configDir = Directory.CreateDirectory(Path.Combine(repo.Path, "solution", ".config")).FullName;
		var configPath = Path.Combine(configDir, "fuxion-tools.json");
		var inputs = VersioningInputsFactory.Create(
			new() { Versioning = new GitVersioningConfig { RepositoryPath = @"..\", FailOnError = true } }, configPath);
		Assert.Equal("1.2.1", inputs.BaseVersion);
	}

	// Informational suffix and CI

	[Theory(DisplayName = "in CI the informational suffix is empty")]
	[InlineData("true", null)]
	[InlineData("1", null)]
	[InlineData("TRUE", null)]
	[InlineData(null, "true")]
	public void Ci_EmptySuffix(string? ciVar, string? githubActions)
	{
		using var ci = new CiEnvironment(ciVar, githubActions);
		using var repo = TaggedMain();
		Assert.Equal(new("1.2.0", ""), FromGit(repo));
	}

	[Fact(DisplayName = "CI=false is not CI")]
	public void CiFalse_IsLocal()
	{
		using var ci = new CiEnvironment("false");
		using var repo = TaggedMain();
		Assert.Equal("+local", FromGit(repo).InformationalSuffix);
	}

	[Fact(DisplayName = "forceCIEnvironment: empty suffix outside CI")]
	public void ForceCi()
	{
		using var ci = CiEnvironment.Local();
		using var repo = TaggedMain();
		Assert.Equal("", FromGit(repo, forceCi: true).InformationalSuffix);
	}

	[Fact(DisplayName = "a configured suffix wins, also in CI")]
	public void ConfiguredSuffix()
	{
		using var ci = new CiEnvironment("true");
		using var repo = TaggedMain();
		Assert.Equal("+dev", FromGit(repo, suffix: "+dev").InformationalSuffix);
	}

	// Other modes

	[Fact(DisplayName = "no versioning config: 0.1.0+local, even in CI")]
	public void NoVersioning()
	{
		using var ci = new CiEnvironment("true");
		Assert.Equal(new("0.1.0", "+local"), VersioningInputsFactory.Create(new()));
	}

	[Fact(DisplayName = "fixed mode: the configured version")]
	public void Fixed()
	{
		using var ci = CiEnvironment.Local();
		Assert.Equal(new("3.4.5", "+local"), VersioningInputsFactory.Create(new() { Versioning = new FixedVersioningConfig { Version = " 3.4.5 " } }));
	}

	[Fact(DisplayName = "fixed mode: 0.1.0 without a version, empty suffix in CI")]
	public void Fixed_DefaultsAndCi()
	{
		using var ci = new CiEnvironment("true");
		Assert.Equal(new("0.1.0", ""), VersioningInputsFactory.Create(new() { Versioning = new FixedVersioningConfig() }));
	}
}
