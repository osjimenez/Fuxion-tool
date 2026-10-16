using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Dotnet;

/// <summary>
/// What the .NET module writes in a root's <c>_fx/dotnet/</c> (design §7.3): <c>dotnet.g.props</c> (who the repository
/// is, the <c>value</c> variables and the <c>props:</c> imports), <c>dotnet.g.targets</c> (the <c>when</c> variables,
/// their constants and the <c>targets:</c> imports) and <c>packages.g.props</c> (a <c>PackageVersion</c> per id and
/// version). Text with '\n'; every file starts with the header (plan P, decision 6).
/// </summary>
public static class DotnetGenerator
{
	static string Header(string sources) => FxHeader.Xml(sources);

	/// <param name="imports">The props: imports: path relative to <c>_fx/dotnet/</c> and expanded condition (null: always).</param>
	/// <param name="requiresWorkspace">
	/// A repository of a workspace that does not build on its own (its <c>dotnet.yaml</c> does not say <c>standalone</c>):
	/// an <c>FxReference</c> that would be a package is an error (plan R, proposal 5), unless the repository says otherwise.
	/// </param>
	public static string Props(DotnetConfig config, FxIdentity identity, IReadOnlyList<(string Path, string? Condition)> imports, string sources,
		bool requiresWorkspace = false)
	{
		var b = Start(sources);
		// who the repository is, before the imports that may use it (plan P, decision 24)
		b.Append("\t<PropertyGroup>\n");
		b.Append($"\t\t<FxRepositoryName>{Escape(identity.Name)}</FxRepositoryName>\n");
		b.Append($"\t\t<FxRepositoryFullName>{Escape(identity.FullName)}</FxRepositoryFullName>\n");
		if (requiresWorkspace)
			b.Append("\t\t<FxRequiresWorkspace Condition=\"'$(FxRequiresWorkspace)' == ''\">true</FxRequiresWorkspace>\n");
		b.Append("\t</PropertyGroup>\n");
		b.Append("\t<PropertyGroup>\n");
		foreach (var variable in config.Variables.Where(v => !v.IsWhen))
			b.Append($"\t\t<{variable.Name}>{Escape(variable.Value!)}</{variable.Name}>\n");
		b.Append("\t</PropertyGroup>\n");
		Imports(b, imports);
		return End(b);
	}

	/// <param name="packageUpdates">
	/// Without central package management: id, version and expanded condition of each governed package, written as
	/// <c>PackageReference Update</c> after the imports (which may add the references).
	/// </param>
	public static string Targets(DotnetConfig config, IReadOnlyDictionary<string, string> expandedWhen, IReadOnlyList<(string Path, string? Condition)> imports,
		IReadOnlyList<(string Id, string Version, string? Condition)> packageUpdates, string sources)
	{
		var b = Start(sources);
		b.Append("\t<PropertyGroup>\n");
		foreach (var variable in config.Variables.Where(v => v.IsWhen))
		{
			b.Append($"\t\t<{variable.Name}>false</{variable.Name}>\n");
			b.Append($"\t\t<{variable.Name} Condition=\"{EscapeAttribute(expandedWhen[variable.Name])}\">true</{variable.Name}>\n");
		}
		b.Append("\t</PropertyGroup>\n");
		// The compilation constants, with the expanded condition too: DefineConstants is read when compiling
		foreach (var variable in config.Variables.Where(v => v.IsWhen && v.Define is not null))
		{
			b.Append($"\t<PropertyGroup Condition=\"{EscapeAttribute(expandedWhen[variable.Name])}\">\n");
			b.Append($"\t\t<DefineConstants>$(DefineConstants);{Escape(variable.Define!)}</DefineConstants>\n");
			b.Append("\t</PropertyGroup>\n");
		}
		Imports(b, imports);
		if (packageUpdates.Count > 0)
		{
			b.Append("\t<ItemGroup>\n");
			foreach (var (id, version, condition) in packageUpdates)
				b.Append($"\t\t<PackageReference Update=\"{EscapeAttribute(id)}\" Version=\"{EscapeAttribute(version)}\"{ConditionAttribute(condition)} />\n");
			b.Append("\t</ItemGroup>\n");
		}
		return End(b);
	}

	static string ConditionAttribute(string? condition) => condition is null ? "" : $" Condition=\"{EscapeAttribute(condition)}\"";

	// The files that dotnet.yaml imports, from the generated file's folder (_fx/dotnet/); a missing file is an error of
	// MSBuild, on purpose (fx doctor reports it too)
	static void Imports(StringBuilder b, IReadOnlyList<(string Path, string? Condition)> imports)
	{
		foreach (var (path, condition) in imports)
			b.Append(condition is null
				? $"\t<Import Project=\"$(MSBuildThisFileDirectory){EscapeAttribute(path)}\" />\n"
				: $"\t<Import Project=\"$(MSBuildThisFileDirectory){EscapeAttribute(path)}\" Condition=\"{EscapeAttribute(condition)}\" />\n");
	}

	/// <param name="packages">Id, version and expanded condition, in the order to write them.</param>
	public static string Packages(IEnumerable<(string Id, string Version, string? Condition)> packages, string sources)
	{
		var b = Start(sources);
		b.Append("\t<ItemGroup>\n");
		foreach (var (id, version, condition) in packages)
			b.Append($"\t\t<PackageVersion Include=\"{EscapeAttribute(id)}\" Version=\"{EscapeAttribute(version)}\"{ConditionAttribute(condition)} />\n");
		b.Append("\t</ItemGroup>\n");
		return End(b);
	}

	/// <summary>
	/// <c>projects.g.props</c> (plan R, decision 1): the projects of the repository by name, as the map of the workspace
	/// does with the mounted ones, so an <c>FxReference</c> to one of them is a <c>ProjectReference</c> also in a clone on
	/// its own. Paths relative to <c>_fx/dotnet/</c>.
	/// </summary>
	/// <param name="projects">Name and path relative to <c>_fx/dotnet/</c> ('/'), in the order to write them.</param>
	public static string Projects(IEnumerable<(string Name, string Path)> projects, string sources)
	{
		var b = Start(sources);
		b.Append("\t<ItemGroup>\n");
		foreach (var (name, path) in projects)
		{
			var n = EscapeAttribute(name);
			var p = "$(MSBuildThisFileDirectory)" + EscapeAttribute(path);
			b.Append($"\t\t<FxRepositoryProject Include=\"{n}\" Path=\"{p}\" />\n");
			b.Append($"\t\t<ProjectReference Include=\"@(FxReference->WithMetadataValue('Identity', '{n}')->'{p}')\" />\n");
		}
		b.Append("\t</ItemGroup>\n");
		return End(b);
	}

	static StringBuilder Start(string sources) => new StringBuilder().Append(Header(sources)).Append("<Project>\n");

	static string End(StringBuilder b) => b.Append("</Project>\n").ToString();

	static string Escape(string value) => SecurityElement.Escape(value).Replace("&apos;", "'").Replace("&quot;", "\"");

	static string EscapeAttribute(string value) => SecurityElement.Escape(value).Replace("&apos;", "'");
}
