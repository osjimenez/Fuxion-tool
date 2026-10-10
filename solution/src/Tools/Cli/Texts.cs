using System.Globalization;
using System.Resources;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Cli;

/// <summary>The texts of the command line (<c>Resources/Strings.resx</c>, <c>Strings.es.resx</c>), in the language of the user.</summary>
public static class Texts
{
	static readonly ResourceManager Resources = new("Fuxion.Tools.Resources.Strings", typeof(Texts).Assembly);

	public static string Get(string key, params object?[] args) => FxText.Format(Resources, key, args, CultureInfo.CurrentUICulture);
}
