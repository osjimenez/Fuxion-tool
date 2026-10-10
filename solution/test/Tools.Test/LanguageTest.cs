using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

static class TestLanguage
{
	/// <summary>The tests run in English, whatever the language of the machine (plan O, decision 7).</summary>
	[ModuleInitializer]
	internal static void English()
	{
		Environment.SetEnvironmentVariable(FxLanguage.Variable, "en");
		FxLanguage.Apply("en");
	}
}

/// <summary>The languages of fx (plan O, O2.4): which one, and that every text is in both.</summary>
public sealed partial class LanguageTest
{
	static Func<string, string?> Env(params (string Name, string Value)[] variables)
		=> name => variables.FirstOrDefault(v => v.Name == name).Value;

	[Theory(DisplayName = "the language: --lang, then FX_LANG, then the system (gettext rules outside Windows)")]
	[InlineData("--lang es", "", "es")]
	[InlineData("--lang=es", "FX_LANG=en", "es")]
	[InlineData("--lang en", "FX_LANG=es", "en")]
	[InlineData("", "FX_LANG=es-ES", "es")]
	[InlineData("", "LANG=es_ES.UTF-8", "es")]
	[InlineData("", "LANG=en_US.UTF-8", "en")]
	[InlineData("", "LC_ALL=es_ES.UTF-8 LANG=en_US.UTF-8", "es")]
	[InlineData("", "LC_MESSAGES=es_ES LANG=en_US", "es")]
	[InlineData("", "LC_ALL=C LANG=es_ES", "en")]
	[InlineData("", "LANG=C.UTF-8", "en")]
	[InlineData("", "LANG=fr_FR LANGUAGE=fr:es", "es")]
	[InlineData("", "LANG=es_ES@euro", "es")]
	[InlineData("", "LANG=fr_FR", "en")]
	[InlineData("", "", "en")]
	[InlineData("--lang fr", "LANG=es_ES", "es")]
	public void Resolve(string args, string variables, string expected)
	{
		var environment = Env(variables.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => (v[..v.IndexOf('=')], v[(v.IndexOf('=') + 1)..])).ToArray());
		Assert.Equal(expected, FxLanguage.Resolve(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), environment, windows: false));
	}

	[GeneratedRegex(@"\{(\d+)")]
	private static partial Regex Placeholder();

	public static TheoryData<string> Resources() => ["core", "cli"];

	static ResourceManager Manager(string which) => which == "core"
		? new("Fuxion.Tools.Core.Resources.Strings", typeof(FxText).Assembly)
		: new("Fuxion.Tools.Resources.Strings", typeof(Texts).Assembly);

	static Dictionary<string, string> Texts(ResourceManager manager, CultureInfo culture)
		=> manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!.Cast<System.Collections.DictionaryEntry>()
			.ToDictionary(e => (string)e.Key, e => (string)e.Value!);

	[Theory(DisplayName = "every text is in English and in Spanish, with the same arguments")]
	[MemberData(nameof(Resources))]
	public void EveryTextInBothLanguages(string which)
	{
		var manager = Manager(which);
		var english = Texts(manager, CultureInfo.InvariantCulture);
		var spanish = Texts(manager, new CultureInfo("es"));
		Assert.NotEmpty(english);
		Assert.Empty(english.Keys.Except(spanish.Keys));
		Assert.Empty(spanish.Keys.Except(english.Keys));
		foreach (var (key, text) in english)
			Assert.True(Placeholder().Matches(text).Select(m => m.Value).Order().SequenceEqual(Placeholder().Matches(spanish[key]).Select(m => m.Value).Order()),
				$"{which}: '{key}' has other arguments in Spanish");
	}

	static string SourceRoot([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "src"));

	[GeneratedRegex(@"Texts\.Get\(""(?<key>[^""]+)""")]
	private static partial Regex CliKey();

	[Fact(DisplayName = "the texts the command line asks for exist (keys written as they are)")]
	public void CliKeysExist()
	{
		var english = Texts(Manager("cli"), CultureInfo.InvariantCulture);
		var keys = Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Tools"), "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.SelectMany(f => CliKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
			.Distinct()
			.ToList();
		Assert.NotEmpty(keys);
		Assert.DoesNotContain(keys, k => !english.ContainsKey(k));
	}

	[Fact(DisplayName = "every diagnostic code has its text (the code, or the code and a variant)")]
	public void CodesHaveTexts()
	{
		var english = Texts(Manager("core"), CultureInfo.InvariantCulture);
		var codes = new[] { typeof(WorkspaceModule), typeof(ModulePropagation), typeof(SolutionGenerator), typeof(WorkspacePropsGenerator), typeof(WorkspaceManifest),
				typeof(Core.Dotnet.DotnetModule), typeof(Core.Dotnet.DotnetYamlReader), typeof(Core.Versioning.VersioningErrorCodes), typeof(Core.Versioning.VersionTagger) }
			.SelectMany(t => t.GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!))
			.Where(c => Regex.IsMatch(c, @"^(workspace|dotnet|version)\.[a-z-]+$"))
			.ToList();
		Assert.NotEmpty(codes);
		// codes whose message is git's or a parser's (FxText.Plain), or that reuse another text
		string[] plain = ["dotnet.invalid-yaml", "version.git-failed", "version.invalid-tag", "workspace.invalid-dotnet", "dotnet.not-generated", "workspace.not-generated"];
		Assert.DoesNotContain(codes.Except(plain), c => !english.ContainsKey(c) && !english.Keys.Any(k => k.StartsWith(c + ".", StringComparison.Ordinal)));
	}

	[Fact(DisplayName = "FxText: translated when written; English for JSON; texts inside texts too")]
	public void Text()
	{
		var text = new FxText("version.failed", new FxText("version.reason.no-tag-stable", "C:/repo"));
		Assert.Equal("Git versioning failed: No version tag found for stable branch in repo 'C:/repo'.", text.English);
		Assert.Equal("Falló el versionado con git: No hay ninguna etiqueta de versión para la rama estable en el repo 'C:/repo'.", text.ToString(new CultureInfo("es")));
		Assert.Equal("as it is", FxText.Plain("as it is").ToString(new CultureInfo("es")));
		Assert.Equal("no.such.key(1)", new FxText("no.such.key", 1).English);
	}
}
