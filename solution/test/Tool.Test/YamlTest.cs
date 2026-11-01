using System.Linq;
using System.Net;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The one YAML reader of fx, on SharpYaml's parser.</summary>
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
		var root = Assert.IsType<YamlMapping>((YamlNode)YamlDocument.Load(yaml));
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
	public void Load_EmptyText_IsNone()
	{
		Assert.True(YamlDocument.Load("") is None);
		Assert.True(YamlDocument.Load("# only a comment\n") is None);
	}


	[Fact]
	public void Load_InvalidYaml_IsAConflictWithTheLineAndAMessageWithoutPositions()
	{
		var loaded = YamlDocument.Load("a: 1\nb: [x, y\nc: 2\n");
		Assert.True(loaded is Error { Type: HttpStatusCode.Conflict });
		var syntax = Assert.Single(loaded.Diagnostics);
		Assert.Equal(YamlDocument.Syntax, syntax.Code);
		Assert.True(syntax.Line >= 2, $"line {syntax.Line}");
		Assert.DoesNotContain("Lin:", syntax.Message.English);
	}


	[Fact]
	public void Load_DuplicateKey_IsAConflict()
	{
		var loaded = YamlDocument.Load("name: a\nname: b\n");
		Assert.True(loaded is Error { Type: HttpStatusCode.Conflict });
		var syntax = Assert.Single(loaded.Diagnostics);
		Assert.Equal(2, syntax.Line);
		Assert.Contains("'name'", syntax.Message.English);
	}

}
