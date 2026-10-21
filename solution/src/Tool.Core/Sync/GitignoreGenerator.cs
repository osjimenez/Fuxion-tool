using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Harness;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;

namespace Fuxion.Tool.Core.Sync;

/// <summary>
/// The <c>.gitignore</c> of a space (plan P, decision 7): a whitelist. Nothing is versioned, at any depth, except
/// fx's configuration, the <c>paths</c> of its <c>space.yaml</c>, what the modules generate and version (the solutions) and what
/// the workspace receives from above (the shares); then
/// the exclusions, the repositories that fall inside versioned folders, and the local files. A path deep inside also
/// brings its folders above (git does not look inside an ignored folder; plan P, P1, detail 5).
/// </summary>
public static class GitignoreGenerator
{
	public const string FileName = ".gitignore";

	/// <param name="versioned">What the modules generate and version, relative to the workspace (<c>Acme.slnx</c>).</param>
	/// <param name="shared">The shares it receives from above, relative to it (<c>res/icon.png</c>).</param>
	public static string Generate(SpaceConfig space, MountsManifest manifest, IEnumerable<string> versioned, IEnumerable<string>? shared = null)
	{
		var b = new StringBuilder(FxHeader.Hash(FxLayout.ConfigFile + " and the modules of _fx"));
		b.Append('\n');
		b.Append("# Nothing is versioned, at any depth, except what follows\n");
		b.Append("*\n");
		var included = (space.Paths ?? []).Where(p => !p.Exclude).ToList();
		var written = new HashSet<string>(StringComparer.Ordinal);
		void Line(StringBuilder to, string line)
		{
			if (written.Add(line))
				to.Append(line).Append('\n');
		}
		void Parents(StringBuilder to, string path)
		{
			var parts = path.TrimEnd('/').Split('/');
			for (var i = 1; i < parts.Length; i++)
				Line(to, $"!/{string.Join('/', parts.Take(i))}/");
		}

		b.Append('\n');
		b.Append("# Always: the configuration of fx, and the files of the coding agents it generates (plan Q)\n");
		b.Append("!/.gitignore\n");
		b.Append($"!/{FxLayout.Folder}/\n");
		b.Append($"!/{FxLayout.Folder}/**\n");
		// versioned whether the run that writes this file writes them or not
		foreach (var file in HarnessModule.Files)
			Line(b, $"!/{file}");
		foreach (var folder in HarnessModule.Folders)
		{
			Parents(b, folder);
			Line(b, $"!/{folder}/");
			Line(b, $"!/{folder}/**");
		}

		if (included.Count > 0)
		{
			b.Append('\n');
			b.Append("# paths (a folder takes two lines: itself and what is inside)\n");
			foreach (var path in included)
			{
				Parents(b, path.Path);
				if (path.IsFolder)
				{
					Line(b, $"!/{path.Path}");
					Line(b, $"!/{path.Path}**");
				}
				else
					Line(b, $"!/{path.Path}");
			}
		}

		var outputs = versioned.Select(v => v.Replace('\\', '/').TrimStart('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (outputs.Count > 0)
		{
			b.Append('\n');
			b.Append("# What the modules generate and version (dotnet: the solutions)\n");
			foreach (var output in outputs)
			{
				Parents(b, output);
				Line(b, $"!/{output}");
			}
		}

		var received = (shared ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (received.Count > 0)
		{
			b.Append('\n');
			b.Append("# What it receives from above (shares)\n");
			foreach (var file in received)
			{
				Parents(b, file);
				Line(b, $"!/{file}");
			}
		}

		var exclusions = (space.Paths ?? []).Where(p => p.Exclude).ToList();
		if (exclusions.Count > 0)
		{
			b.Append('\n');
			b.Append("# paths with exclude: true (exceptions inside what is versioned)\n");
			foreach (var path in exclusions)
				b.Append($"/{path.Path}\n");
		}

		var inside = manifest.Mounts.Where(r => space.Versions(r.Path + "/")).ToList();
		if (inside.Count > 0)
		{
			b.Append('\n');
			b.Append("# Repositories inside versioned folders\n");
			foreach (var repo in inside)
				b.Append($"/{repo.Path}/\n");
		}

		b.Append('\n');
		b.Append("# Local files, at any level\n");
		b.Append($"{FxLayout.LocalPrefix}*\n");
		return b.ToString();
	}
}
