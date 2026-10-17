using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fuxion.Tool.Core.Layout;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The read layer of the core (plan O, O4.3a): the disk, or pending texts in place of its files.</summary>
public sealed class FxFilesTest : IDisposable
{
	readonly string _root = Path.Combine(TempGitRepository.TempRoot, $"fx-files-{Guid.NewGuid():N}"[..20]);

	public FxFilesTest()
	{
		Directory.CreateDirectory(Path.Combine(_root, "_fx", "dotnet"));
		File.WriteAllText(Path.Combine(_root, "_fx", "dotnet", "dotnet.yaml"), "disk\n");
	}

	public void Dispose() => Directory.Delete(_root, true);

	string P(params string[] parts) => Path.Combine([_root, .. parts]);

	[Fact(DisplayName = "without an overlay, the disk; with one, its texts in place of the files, and the new ones with their folders")]
	public async Task Overlay()
	{
		var yaml = P("_fx", "dotnet", "dotnet.yaml");
		var added = P("_fx", "workspace", "workspace.yaml");
		Assert.Equal("disk\n", FxFiles.ReadAllText(yaml));
		Assert.False(FxFiles.Exists(added));
		Assert.False(FxFiles.Overlaid);

		using (FxFiles.Overlay([KeyValuePair.Create(yaml, "pending\n"), KeyValuePair.Create(added, "new\n")]))
		{
			Assert.True(FxFiles.Overlaid);
			Assert.Equal("pending\n", FxFiles.ReadAllText(yaml));
			Assert.Equal("pending\n"u8.ToArray(), FxFiles.ReadAllBytes(yaml));
			Assert.True(FxFiles.Exists(added));
			Assert.True(FxFiles.DirectoryExists(P("_fx", "workspace")));
			// its folder lists it, and its parent lists the folder; the disk, untouched
			Assert.Equal([added], FxFiles.EnumerateFiles(P("_fx", "workspace")));
			Assert.Equal(2, FxFiles.EnumerateFiles(P("_fx"), "*.yaml", SearchOption.AllDirectories).Count());
			Assert.Empty(FxFiles.EnumerateFiles(P("_fx"), "*.yaml"));
			Assert.Equal(["dotnet", "workspace"], FxFiles.EnumerateDirectories(P("_fx")).Select(Path.GetFileName).Order());
			Assert.Equal("disk\n", File.ReadAllText(yaml));
			Assert.False(Directory.Exists(P("_fx", "workspace")));

			// over it, another one wins; disposing it brings back the first
			using (FxFiles.Overlay([KeyValuePair.Create(yaml, "inner\n")]))
				Assert.Equal("inner\n", FxFiles.ReadAllText(yaml));
			Assert.Equal("pending\n", FxFiles.ReadAllText(yaml));

			// tasks and parallel loops of the core see it
			Assert.Equal("pending\n", await Task.Run(() => FxFiles.ReadAllText(yaml), TestContext.Current.CancellationToken));
			var seen = new List<string>();
			Parallel.For(0, 4, _ =>
			{
				lock (seen)
					seen.Add(FxFiles.ReadAllText(yaml));
			});
			Assert.All(seen, s => Assert.Equal("pending\n", s));
		}
		Assert.False(FxFiles.Overlaid);
		Assert.Equal("disk\n", FxFiles.ReadAllText(yaml));
		Assert.False(FxFiles.DirectoryExists(P("_fx", "workspace")));
	}
}
