using System;
using System.IO;
using Fuxion.Tool.Core.Layout;
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
}
