using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Fuxion.Tools.Core.Versioning;

public static class PropsFileReader
{
	public static string? TryReadFingerprint(string propsFilePath)
	{
		if (string.IsNullOrWhiteSpace(propsFilePath) || !File.Exists(propsFilePath))
			return null;

		try
		{
			var doc = XDocument.Load(propsFilePath);
			var root = doc.Root;
			if (root is null)
				return null;

			XNamespace ns = root.Name.Namespace;
			var fingerprint = root
				.Elements(ns + "PropertyGroup")
				.Elements(ns + "FuxionToolsVersioningFingerprint")
				.FirstOrDefault();

			return fingerprint?.Value;
		}
		catch
		{
			return null;
		}
	}
}
