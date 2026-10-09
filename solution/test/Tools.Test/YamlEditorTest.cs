using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tools.Core.Yaml;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>
/// The YAML edit engine (plan O, O2.2), with the checks of the O1 probe: every edit, on LF and on CRLF, reparses, leaves
/// the text outside its range byte for byte, keeps the line endings and the comments (but those it takes with it), and
/// does what it says. The fixtures are copies of the workspace's manifest and dotnet.yaml (as of 09-oct) and a
/// synthetic file with comments everywhere.
/// </summary>
public sealed class YamlEditorTest
{
	sealed record Scenario(string Fixture, Func<YamlEditor, YamlEdit> Plan, Func<YamlEditor, bool> Expect, int RemovedComments = 0);

	static string? Val(YamlEditor e, string path) => (e.Resolve(path) as YamlScalar)?.Value;

	static int Count(YamlEditor e, string path) => e.Resolve(path) switch { YamlSequence s => s.Items.Count, YamlMapping m => m.Entries.Count, _ => -1 };

	static string Keys(YamlEditor e, string path) => string.Join(",", ((YamlMapping)e.Resolve(path)!).Entries.Select(x => ((YamlScalar)x.Key).Value));

	static string Items(YamlEditor e, string path) => string.Join(",", ((YamlSequence)e.Resolve(path)!).Items.Select(x => ((YamlScalar)x).Value));

	static int Col(YamlEditor e, string path) => e.Resolve(path)!.Column;

	static int KeyCol(YamlEditor e, string map, string key) => ((YamlMapping)e.Resolve(map)!).Entry(key)!.Key.Column;

	static YamlScalarStyle Style(YamlEditor e, string path) => ((YamlScalar)e.Resolve(path)!).Style;

	static readonly Dictionary<string, Scenario> Scenarios = new()
	{
		// _fx/workspace.yaml
		["W-a1 set scalar workspace.major"] = new("workspace",
			e => e.SetScalar("workspace.major", new YamlValue.Raw("12")), e => Val(e, "workspace.major") == "12"),
		["W-a2 set scalar plus.solution"] = new("workspace",
			e => e.SetScalar("repositories[name=plus].solution", "FxPlus.Next.slnx"), e => Val(e, "repositories[name=plus].solution") == "FxPlus.Next.slnx"),
		["W-b1 add a repository"] = new("workspace",
			e => e.Append("repositories", new YamlValue.Map(("name", "sandbox"), ("path", "sandbox/repo"), ("url", "https://github.com/osjimenez/Fuxion-sandbox.git"),
				("standalone", new YamlValue.Raw("false")), ("dependsOn", new YamlValue.Flow("oss")), ("tags", new YamlValue.Flow("dotnet", "fuxion-build")))),
			e => Count(e, "repositories") == 8 && Val(e, "repositories[7].name") == "sandbox" && Items(e, "repositories[name=sandbox].tags") == "dotnet,fuxion-build"
			     && Col(e, "repositories[name=sandbox]") == Col(e, "repositories[name=docs]") && Col(e, "repositories[name=sandbox].tags") == Col(e, "repositories[name=docs].tags")),
		["W-b2 add a module"] = new("workspace",
			e => e.AddKey("modules", "python", new YamlValue.Map(("tags", new YamlValue.Flow("py")))),
			e => Items(e, "modules.python.tags") == "py" && Keys(e, "modules") == "dotnet,python"),
		["W-c1 add a tag"] = new("workspace",
			e => e.Append("repositories[name=plus].tags", "windows"), e => Items(e, "repositories[name=plus].tags") == "dotnet,fuxion-build,uses-fuxion,windows"),
		["W-c2 add a folder"] = new("workspace",
			e => e.Append("folders", "_tmp"), e => Items(e, "folders") == "_docs,_temp,_tmp"),
		["W-d1 insert a key after another"] = new("workspace",
			e => e.AddKey("repositories[name=tool]", "mount", "manual", after: "url"),
			e => Keys(e, "repositories[name=tool]") == "name,path,url,mount,standalone,solution,tags" && Val(e, "repositories[name=tool].mount") == "manual"),
		["W-d2 insert a key at the end"] = new("workspace",
			e => e.AddKey("workspace", "fx", "1.2.0"), e => Keys(e, "workspace") == "name,major,fx" && Val(e, "workspace.fx") == "1.2.0"),
		["W-e1 remove a repository"] = new("workspace",
			e => e.Remove("repositories[name=next]"), e => Count(e, "repositories") == 6 && e.Resolve("repositories[name=next]") is null),
		["W-e2 remove a key"] = new("workspace",
			e => e.Remove("repositories[name=plus].standalone"), e => Keys(e, "repositories[name=plus]") == "name,path,url,mount,solution,tags"),
		["W-e3 remove a tag"] = new("workspace",
			e => e.Remove("repositories[name=oss].tags[=public]"), e => Items(e, "repositories[name=oss].tags") == "dotnet,fuxion-build"),
		["W-e4 remove the first key of an item"] = new("workspace",
			e => e.Remove("repositories[name=docs].name"), e => Keys(e, "repositories[6]") == "path,url,standalone,tags" && Col(e, "repositories[6]") == Col(e, "repositories[5]")),

		// _fx/dotnet.yaml
		["D-a3 set a dotted key"] = new("dotnet",
			e => e.SetScalar("sdks[\"Fuxion.Tools.Sdk\"]", "0.1.13"), e => Val(e, "sdks[\"Fuxion.Tools.Sdk\"]") == "0.1.13"),
		["D-a4 set a package version"] = new("dotnet",
			e => e.SetScalar("packages[ids=PolySharp].versions[0].version", "1.17.0"), e => Val(e, "packages[ids=PolySharp].versions[0].version") == "1.17.0"),
		["D-a5 set a double-quoted condition"] = new("dotnet",
			e => e.SetScalar("variables[name=IsNet11].when", "'$(TargetFramework)' == 'net11.0'"),
			e => Val(e, "variables[name=IsNet11].when") == "'$(TargetFramework)' == 'net11.0'" && Style(e, "variables[name=IsNet11].when") == YamlScalarStyle.DoubleQuoted),
		["D-b3 add a package with nested versions"] = new("dotnet",
			e => e.Append("packages", new YamlValue.Map(("ids", new YamlValue.Flow("Spectre.Console")), ("versions", new YamlValue.Block(new YamlValue.Map(("version", "0.50.0")))))),
			e => Count(e, "packages") == 7 && Val(e, "packages[6].versions[0].version") == "0.50.0" && Col(e, "packages[6].versions[0]") == Col(e, "packages[5].versions[0]")),
		["D-c3 add a package id"] = new("dotnet",
			e => e.Append("packages[ids=Microsoft.AspNetCore.Mvc.Testing].ids", "Microsoft.Extensions.Options"),
			e => Count(e, "packages[3].ids") == 16 && Val(e, "packages[3].ids[15]") == "Microsoft.Extensions.Options" && Col(e, "packages[3].ids[15]") == Col(e, "packages[3].ids[0]")),
		["D-c4 add an import tag"] = new("dotnet",
			e => e.Append("imports[targets=dotnet/fuxion-analyzers.targets].tags", "uses-fuxion-plus"),
			e => Items(e, "imports[4].tags") == "uses-fuxion,uses-fuxion-plus"),
		["D-d3 insert a key into a one-key item"] = new("dotnet",
			e => e.AddKey("imports[props=dotnet/default.props]", "tags", new YamlValue.Flow("uses-fuxion")),
			e => Keys(e, "imports[0]") == "props,tags" && KeyCol(e, "imports[0]", "tags") == KeyCol(e, "imports[1]", "targets")),
		["D-e5 remove a package with its comment"] = new("dotnet",
			e => e.Remove("packages[ids=Microsoft.AspNetCore.Mvc.Testing]"), e => Count(e, "packages") == 5 && Val(e, "packages[3].ids[0]") == "Microsoft.EntityFrameworkCore", 1),
		["D-e6 remove a package id"] = new("dotnet",
			e => e.Remove("packages[ids=Microsoft.EntityFrameworkCore].ids[=Microsoft.EntityFrameworkCore.InMemory]"),
			e => Items(e, "packages[4].ids") == "Microsoft.EntityFrameworkCore,Microsoft.EntityFrameworkCore.Sqlite,Microsoft.EntityFrameworkCore.SqlServer"),
		["D-e7 remove a variable"] = new("dotnet",
			e => e.Remove("variables[name=IsNet10]"), e => Count(e, "variables") == 15 && e.Resolve("variables[name=IsNet10]") is null),

		// synthetic: comments everywhere, flow and block, quoting
		["S-a6 set a double-quoted scalar"] = new("synthetic",
			e => e.SetScalar("name", "probe-2"), e => Val(e, "name") == "probe-2" && Style(e, "name") == YamlScalarStyle.DoubleQuoted),
		["S-a7 set a single-quoted scalar"] = new("synthetic",
			e => e.SetScalar("title", "It's edited"), e => Val(e, "title") == "It's edited" && Style(e, "title") == YamlScalarStyle.SingleQuoted),
		["S-a8 set an empty value"] = new("synthetic",
			e => e.SetScalar("empty", "now set"), e => Val(e, "empty") == "now set"),
		["S-a9 set a non-ASCII value"] = new("synthetic",
			e => e.SetScalar("items[id=third].note", "ñandú 🦜"), e => Val(e, "items[id=third].note") == "ñandú 🦜"),
		["S-b4 add an item that needs quotes"] = new("synthetic",
			e => e.Append("items", new YamlValue.Map(("id", "fourth"), ("note", "a # not a comment"), ("flags", new YamlValue.Flow()))),
			e => Count(e, "items") == 4 && Val(e, "items[3].note") == "a # not a comment" && Count(e, "items[3].flags") == 0),
		["S-b5 add a nested mapping"] = new("synthetic",
			e => e.AddKey("nested.level1", "map", new YamlValue.Map(("k1", "v1"), ("k2", new YamlValue.Flow(new YamlValue.Raw("1"), new YamlValue.Raw("2"))))),
			e => Keys(e, "nested.level1") == "key,list,other,map" && Items(e, "nested.level1.map.k2") == "1,2" && KeyCol(e, "nested.level1.map", "k1") == KeyCol(e, "nested.level1", "key") + 2),
		["S-c5 add to a flow sequence with mixed quoting"] = new("synthetic",
			e => e.Append("tags", "delta"), e => Items(e, "tags") == "alpha,beta,gamma,delta"),
		["S-c6 add to an empty flow sequence"] = new("synthetic",
			e => e.Append("none", "first"), e => Items(e, "none") == "first"),
		["S-c7 add to a block sequence with comments"] = new("synthetic",
			e => e.Append("nested.level1.list", "four"), e => Items(e, "nested.level1.list") == "one,two,three,four" && Col(e, "nested.level1.list[3]") == Col(e, "nested.level1.list[0]")),
		["S-c8 add to an indentless sequence"] = new("synthetic",
			e => e.Append("indentless", "c"), e => Items(e, "indentless") == "a,b,c" && Col(e, "indentless[2]") == Col(e, "indentless[0]")),
		["S-c9 add a key to a flow mapping"] = new("synthetic",
			e => e.AddKey("point", "z", new YamlValue.Raw("3")), e => Keys(e, "point") == "x,y,z"),
		["S-d4 insert a key after the first"] = new("synthetic",
			e => e.AddKey("items[id=second]", "id2", "second-bis", after: "id"), e => Keys(e, "items[id=second]") == "id,id2,note"),
		["S-e8 remove an item with its comments"] = new("synthetic",
			e => e.Remove("items[id=second]"), e => Count(e, "items") == 2 && Val(e, "items[1].id") == "third", 2),
		["S-e9 remove an item between comments"] = new("synthetic",
			e => e.Remove("nested.level1.list[=two]"), e => Items(e, "nested.level1.list") == "one,three", 1),
		["S-e10 remove a flow item"] = new("synthetic",
			e => e.Remove("tags[=beta]"), e => Items(e, "tags") == "alpha,gamma"),
		["S-e11 remove a flow mapping entry"] = new("synthetic",
			e => e.Remove("point.x"), e => Keys(e, "point") == "y"),
		["S-e12 remove the first key of an item"] = new("synthetic",
			e => e.Remove("items[id=first].id"), e => Keys(e, "items[0]") == "note,flags", 1)
	};

	public static TheoryData<string, string> Edits()
	{
		var data = new TheoryData<string, string>();
		foreach (var newLine in new[] { "LF", "CRLF" })
			foreach (var id in Scenarios.Keys)
				data.Add(newLine, id);
		return data;
	}

	public static TheoryData<string, string> Fixtures()
	{
		var data = new TheoryData<string, string>();
		foreach (var newLine in new[] { "LF", "CRLF" })
			foreach (var fixture in new[] { "workspace", "dotnet", "synthetic" })
				data.Add(newLine, fixture);
		return data;
	}

	static string Fixture(string name, string newLine)
	{
		var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "YamlFixtures", $"{name}.yaml")).Replace("\r\n", "\n");
		return newLine == "CRLF" ? text.Replace("\n", "\r\n") : text;
	}

	static string LineEndings(string text)
	{
		int crlf = 0, lf = 0;
		for (var i = 0; i < text.Length; i++)
			if (text[i] == '\n')
			{
				if (i > 0 && text[i - 1] == '\r')
					crlf++;
				else
					lf++;
			}
		return crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF" : "LF";
	}

	[Theory]
	[MemberData(nameof(Edits))]
	public void Edit_ChangesOnlyItsRange(string newLine, string id)
	{
		var scenario = Scenarios[id];
		var before = YamlEditor.Parse(Fixture(scenario.Fixture, newLine));
		var edit = scenario.Plan(before);
		var after = before.Apply(edit);

		// Outside the edit, byte for byte
		var utf8 = new UTF8Encoding(false);
		Assert.Equal(utf8.GetBytes(before.Text[..edit.Start]), utf8.GetBytes(after.Text[..edit.Start]));
		Assert.Equal(utf8.GetBytes(before.Text[(edit.Start + edit.Length)..]), utf8.GetBytes(after.Text[(edit.Start + edit.Text.Length)..]));
		Assert.Equal(before.Text.Length - edit.Length + edit.Text.Length, after.Text.Length);
		// Reparses, same line endings
		Assert.NotNull(YamlDocument.Load(after.Text));
		Assert.Equal(newLine, LineEndings(after.Text));
		// The comments outside the range stay; those inside go, as many as expected
		var kept = before.Comments().Where(c => c.Start < edit.Start || c.Start >= edit.Start + edit.Length).Select(c => c.Text).ToList();
		Assert.Equal(kept, after.Comments().Select(c => c.Text));
		Assert.Equal(scenario.RemovedComments, before.Comments().Count - kept.Count);
		Assert.Equal(scenario.RemovedComments, edit.RemovedComments);
		// And it does what it says
		Assert.True(scenario.Expect(after), $"{id}: {edit.What}");
	}

	[Theory]
	[MemberData(nameof(Fixtures))]
	public void Edits_OneAfterAnother_KeepEverythingElse(string newLine, string fixture)
	{
		var original = YamlEditor.Parse(Fixture(fixture, newLine));
		var editor = original;
		var removed = 0;
		foreach (var scenario in Scenarios.Values.Where(s => s.Fixture == fixture))
		{
			// Planned on the current text: the positions are the new ones
			var edit = scenario.Plan(editor);
			removed += edit.RemovedComments;
			editor = editor.Apply(edit);
		}
		Assert.Equal(newLine, LineEndings(editor.Text));
		Assert.Equal(original.Comments().Count - removed, editor.Comments().Count);
		Assert.NotNull(YamlDocument.Load(editor.Text));
	}

	[Fact]
	public void ReadFile_WriteFile_KeepTheByteOrderMark()
	{
		var path = Path.Combine(Path.GetTempPath(), $"fx-yaml-{Guid.NewGuid():N}.yaml");
		try
		{
			File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# café\nname: a\n")]);
			var editor = YamlEditor.ReadFile(path);
			Assert.True(editor.HasBom);
			editor.Apply(editor.SetScalar("name", "b")).WriteFile(path);
			Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# café\nname: b\n")], File.ReadAllBytes(path));

			File.WriteAllBytes(path, Encoding.UTF8.GetBytes("name: a\n"));
			editor = YamlEditor.ReadFile(path);
			Assert.False(editor.HasBom);
			editor.WriteFile(path);
			Assert.Equal(Encoding.UTF8.GetBytes("name: a\n"), File.ReadAllBytes(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Comments_LeaveOutWhatIsInsideScalars()
	{
		// SharpYaml 3.15.0 gives "#d" for c#d (no comment) and one "# y\" # real" for "x # y" # real (the real one inside)
		var editor = YamlEditor.Parse("a: \"x # y\" # real\nb: c#d\n# real too\n");
		Assert.Equal(["# real", "# real too"], editor.Comments().Select(c => c.Text));
		Assert.Equal(11, editor.Comments()[0].Start);
	}

	[Fact]
	public void Apply_InvalidResult_Throws()
	{
		var editor = YamlEditor.Parse("a: [1, 2]\n");
		var ex = Assert.Throws<YamlSyntaxException>(() => editor.Apply(new YamlEdit(4, 0, "[", "break it")));
		Assert.Contains("break it", ex.Message);
		Assert.Equal("a: [1, 2]\n", editor.Text);
	}

	[Theory]
	[InlineData("plain text", "plain text")]
	[InlineData("https://github.com/osjimenez/Fuxion.git", "https://github.com/osjimenez/Fuxion.git")]
	[InlineData("a: b", "\"a: b\"")]
	[InlineData("true", "\"true\"")]
	[InlineData("1.5", "\"1.5\"")]
	[InlineData("- dash", "\"- dash\"")]
	[InlineData("x # y", "\"x # y\"")]
	[InlineData("say \"hi\"", "say \"hi\"")]
	[InlineData("\"hi\" there", "\"\\\"hi\\\" there\"")]
	[InlineData("", "\"\"")]
	public void Quote_OnlyWhenNeeded(string value, string expected) => Assert.Equal(expected, YamlEditor.Quote(value));

	[Fact]
	public void Path_Malformed_Throws() => Assert.Throws<FormatException>(() => YamlEditor.Parse("a: 1\n").Resolve("a[x"));
}
