using System.Linq;
using Fuxion.Tools.Core.Yaml;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The one YAML reader of fx, on SharpYaml's parser (plan O, O2.1).</summary>
public sealed class YamlTest
{
	[Fact]
	public void Load_GivesTheTreeWithOneBasedPositions()
	{
		const string yaml = """
			# a comment
			repositories:
			  - name: oss
			    tags: [dotnet, public]
			  - name: 'plus'
			""";
		var root = Assert.IsType<YamlMapping>(YamlDocument.Load(yaml));
		var repos = Assert.IsType<YamlSequence>(root["repositories"]);
		Assert.Equal(2, repos.Items.Count);
		var oss = Assert.IsType<YamlMapping>(repos.Items[0]);
		Assert.Equal(3, oss.Line);
		Assert.Equal(5, oss.Column);
		var tags = Assert.IsType<YamlSequence>(oss["tags"]);
		Assert.True(tags.IsFlow);
		Assert.Equal(["dotnet", "public"], tags.Items.Cast<YamlScalar>().Select(t => t.Value));
		Assert.Equal(']', yaml[tags.End - 1]);
		var plus = Assert.IsType<YamlScalar>(Assert.IsType<YamlMapping>(repos.Items[1])["name"]);
		Assert.Equal("plus", plus.Value);
		Assert.True(plus.IsQuoted);
		Assert.Same(repos, oss.Parent);
	}

	[Fact]
	public void Load_EmptyText_IsNull()
	{
		Assert.Null(YamlDocument.Load(""));
		Assert.Null(YamlDocument.Load("# only a comment\n"));
	}

	[Fact]
	public void Load_InvalidYaml_ThrowsWithTheLineAndAMessageWithoutPositions()
	{
		var ex = Assert.Throws<YamlSyntaxException>(() => YamlDocument.Load("a: 1\nb: [x, y\nc: 2\n"));
		Assert.True(ex.Line >= 2, $"line {ex.Line}");
		Assert.DoesNotContain("Lin:", ex.Message);
	}

	[Fact]
	public void Load_DuplicateKey_Throws()
	{
		var ex = Assert.Throws<YamlSyntaxException>(() => YamlDocument.Load("name: a\nname: b\n"));
		Assert.Equal(2, ex.Line);
		Assert.Contains("'name'", ex.Message);
	}
}
