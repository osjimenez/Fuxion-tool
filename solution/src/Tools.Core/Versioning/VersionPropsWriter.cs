using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading;

namespace Fuxion.Tools.Core.Versioning;

public static class VersionPropsWriter
{
	public static void WriteMsBuildPropsFile(string outputPath, VersionProps props)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		ArgumentNullException.ThrowIfNull(props);

		var content = $"""
<Project>
	<PropertyGroup>
		<FuxionToolsVersioningFingerprint>{EscapeXml(props.Fingerprint)}</FuxionToolsVersioningFingerprint>
		<Version>{EscapeXml(props.Version)}</Version>
		<PackageVersion>{EscapeXml(props.PackageVersion)}</PackageVersion>
		<AssemblyVersion>{EscapeXml(props.AssemblyVersion)}</AssemblyVersion>
		<FileVersion>{EscapeXml(props.FileVersion)}</FileVersion>
		<InformationalVersion>{EscapeXml(props.InformationalVersion)}</InformationalVersion>
	</PropertyGroup>
</Project>
""";

		WriteFileAtomically(outputPath, content);
	}

	static void WriteFileAtomically(string path, string content)
	{
		var mutexName = GetMutexName(path);
		using var mutex = new Mutex(false, mutexName);
		if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
			throw new IOException($"Timed out waiting for version props lock '{mutexName}'.");
		try
		{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		var tmp = path + ".tmp";
		File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		if (File.Exists(path))
		{
			try
			{
				File.Replace(tmp, path, destinationBackupFileName: null);
				return;
			}
			catch
			{
				// Fall back to non-atomic replace if File.Replace isn't supported.
			}
		}

		File.Copy(tmp, path, overwrite: true);
		File.Delete(tmp);
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	static string GetMutexName(string path)
	{
		var bytes = Encoding.UTF8.GetBytes(path);
		var hash = Convert.ToHexString(SHA256.HashData(bytes));
		return $"Global\\FuxionToolsVersionProps_{hash}";
	}

	static string EscapeXml(string value)
	{
		return value
			.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal)
			.Replace("\"", "&quot;", StringComparison.Ordinal)
			.Replace("'", "&apos;", StringComparison.Ordinal);
	}
}
