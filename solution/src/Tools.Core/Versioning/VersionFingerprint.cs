using System;
using System.Security.Cryptography;
using System.Text;

namespace Fuxion.Tools.Core.Versioning;

public static class VersionFingerprint
{
	public static string Compute(VersioningInputs inputs)
	{
		var payload = $"schema=2;base={inputs.BaseVersion};info={inputs.InformationalSuffix};";
		var bytes = Encoding.UTF8.GetBytes(payload);
		var hash = SHA256.HashData(bytes);
		return Convert.ToHexString(hash);
	}
}
