using System;
using System.IO;
using System.Linq;
using System.Net;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The paths of fx (<see cref="FxLayout"/>).</summary>
public sealed class LayoutTest
{
	[Fact(DisplayName = "Real: a folder named through a symbolic link is its target, as git names it (macOS: /var is /private/var)")]
	public void Real()
	{
		Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows Real is the full path as it is");
		var root = TempGitRepository.TempFolder("fx-real-");
		try
		{
			var target = Directory.CreateDirectory(Path.Combine(root, "target", "inside")).FullName;
			var link = Path.Combine(root, "link");
			Directory.CreateSymbolicLink(link, Path.Combine(root, "target"));
			Assert.Equal(target, FxLayout.Real(Path.Combine(link, "inside")));
			Assert.Equal(target + Path.DirectorySeparatorChar, FxLayout.Real(Path.Combine(link, "inside") + Path.DirectorySeparatorChar));
			// what does not exist yet keeps its names after the real part
			Assert.Equal(Path.Combine(target, "new", "x"), FxLayout.Real(Path.Combine(link, "inside", "new", "x")));
			// the temporary folder of the tests is real already (on macOS, /private/var…)
			Assert.Equal(TempGitRepository.TempRoot, FxLayout.Real(TempGitRepository.TempRoot));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Fact(DisplayName = "Real on Windows: the full path, as it is")]
	public void Real_Windows()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "only on Windows");
		Assert.Equal(Path.GetFullPath("x"), FxLayout.Real("x"));
	}

	// the files of fx, read with their problems

	[Fact(DisplayName = "a file of fx: invalid YAML, a document that is not a mapping or a newer schema are a conflict with its own code; no schema, a warning")]
	public void Reader_Load()
	{
		var invalid = new FxYamlReader("test.invalid", "a.yaml").Load("a: [x\n");
		Assert.True(invalid is Error { Type: HttpStatusCode.Conflict });
		Assert.Equal(("test.invalid", "a.yaml"), (invalid.Diagnostics[0].Code, invalid.Diagnostics[0].File));
		Assert.True(new FxYamlReader("test.invalid", "a.yaml").Load("- a\n") is Error);
		Assert.Equal(FxYamlReader.SchemaNewer, new FxYamlReader("test.invalid", "a.yaml").Load("schema: 2.0.0\n").Diagnostics.Single().Code);

		var reader = new FxYamlReader("test.invalid", "a.yaml");
		var missing = reader.Load("a: 1\n");
		Assert.True(missing is YamlMapping);
		Assert.Equal(FxYamlReader.SchemaMissing, Assert.Single(missing.Diagnostics).Code);
		Assert.True(reader.Outcome(1, HttpStatusCode.Conflict) is int);
		reader.Error(null, "x");
		var outcome = reader.Outcome(1, HttpStatusCode.Conflict);
		Assert.Equal([FxYamlReader.SchemaMissing, "test.invalid"], outcome.Diagnostics.Select(d => d.Code));
		Assert.True(outcome is Error { Type: HttpStatusCode.Conflict });
	}

	[Fact(DisplayName = "space.yaml with an error: read, a conflict with all it found; loaded, what can be read of it")]
	public void Space_ReadOrLoad()
	{
		const string yaml = "schema: 1.0.0\nname: acme\ntags:\n  - nope\n";
		var read = SpaceConfig.Read(yaml, FxLayout.ConfigFile);
		Assert.True(read is Error { Type: HttpStatusCode.Conflict });
		Assert.Equal((SpaceConfig.Invalid, 4), (read.Diagnostics.Single().Code, read.Diagnostics.Single().Line));
		Assert.Equal("acme", SpaceConfig.Parse(yaml, FxLayout.ConfigFile).Name);

		var root = TempGitRepository.TempFolder("fx-layout-");
		try
		{
			var node = new FxNode(root);
			Directory.CreateDirectory(Path.Combine(root, "_fx"));
			Assert.True(node.ReadSelfIdentity() is Error);
			Assert.Equal(FxNode.NameMissing, node.ReadSelfIdentity().Diagnostics.Single().Code);
			File.WriteAllText(Path.Combine(root, "_fx", "space.yaml"), yaml);
			Assert.Equal("acme", node.Config().Name);
			Assert.True(node.ReadConfig() is Error);
			Assert.Equal("acme", node.SelfIdentity().Name);
			Assert.True(SpaceConfig.ReadFile(Path.Combine(root, "nothing")) is SpaceConfig { Name: null });
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
