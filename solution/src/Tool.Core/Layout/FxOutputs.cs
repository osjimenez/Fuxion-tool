using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Layout;

/// <summary>
/// The files a run of fx owns and what happened to each (written, unchanged, would be written, refused), with their
/// paths relative to where the run started. Text is compared without minding the line endings (git may have turned them
/// into CRLF) and written with the system's; binaries, byte for byte. A file outside <c>_fx/.inherited/</c> is only
/// overwritten if it has the header (<see cref="FxHeader.Mark"/>), or with <c>--adopt</c>.
/// </summary>
public sealed class FxOutputs(string displayRoot, bool write, bool adopt, FxDiagnostics diagnostics)
{
	public const string NotGenerated = "fx.not-generated";

	public List<SyncFile> Files { get; } = [];

	public bool Write => write;

	/// <summary>Whether fx takes over files that are not its own (<c>--adopt</c>).</summary>
	public bool Adopt => adopt;

	public string Display(string path) => FxLayout.Relative(displayRoot, path);

	/// <summary>A file fx does not write because it is not its own; the caller says why.</summary>
	public void Refused(string path) => Files.Add(new(Display(path), SyncFileStatus.Refused));

	/// <summary>Whether <paramref name="path"/> exists and holds <paramref name="content"/>: text without minding the line endings, binaries byte for byte.</summary>
	public static bool Same(string path, byte[] content)
	{
		if (!FxFiles.Exists(path))
			return false;
		return IsText(content)
			? Normalize(FxFiles.ReadAllText(path)) == Normalize(new UTF8Encoding(false).GetString(content).TrimStart('﻿'))
			: FxFiles.ReadAllBytes(path).AsSpan().SequenceEqual(content);
	}

	/// <summary>A file fx generates, with its header in <paramref name="content"/> ('\n').</summary>
	public void Generated(string path, string content)
	{
		if (FxFiles.Exists(path))
		{
			var current = FxFiles.ReadAllText(path);
			if (Normalize(current) == content)
			{
				Files.Add(new(Display(path), SyncFileStatus.Unchanged));
				return;
			}
			if (!FxHeader.IsGenerated(current) && !adopt)
			{
				Files.Add(new(Display(path), SyncFileStatus.Refused));
				diagnostics.Add(FxDiagnostic.Error(NotGenerated, new(NotGenerated), Display(path),
					fix: new FxFix("fx sync --adopt", new($"{NotGenerated}.fix"))));
				return;
			}
		}
		SaveText(path, content);
	}

	/// <summary>A file that is fx's because of where it is (<c>_fx/.inherited/</c>, the local files): no header needed.</summary>
	public void Owned(string path, byte[] content)
	{
		if (IsText(content))
		{
			var text = Normalize(new UTF8Encoding(false).GetString(content).TrimStart('﻿'));
			if (FxFiles.Exists(path) && Normalize(FxFiles.ReadAllText(path)) == text)
			{
				Files.Add(new(Display(path), SyncFileStatus.Unchanged));
				return;
			}
			SaveText(path, text);
			return;
		}
		if (FxFiles.Exists(path) && FxFiles.ReadAllBytes(path).AsSpan().SequenceEqual(content))
		{
			Files.Add(new(Display(path), SyncFileStatus.Unchanged));
			return;
		}
		if (write)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, content);
		}
		else
			FxFiles.Stage(path, content);
		Files.Add(new(Display(path), write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	/// <summary>Removes a file that is fx's and is no longer wanted.</summary>
	public void Delete(string path)
	{
		if (!FxFiles.Exists(path))
			return;
		if (write)
			File.Delete(path);
		else
			FxFiles.Stage(path, null);
		Files.Add(new(Display(path), write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	/// <summary>Removes the empty folders under <paramref name="folder"/> (and it, if it ends up empty).</summary>
	public void Prune(string folder)
	{
		if (!write || !FxFiles.DirectoryExists(folder))
			return;
		foreach (var sub in FxFiles.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
			if (!Directory.EnumerateFileSystemEntries(sub).Any())
				Directory.Delete(sub);
		if (!Directory.EnumerateFileSystemEntries(folder).Any())
			Directory.Delete(folder);
	}

	void SaveText(string path, string content)
	{
		if (write)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
		}
		// not written: what is read from there next, in this run, is what it would be
		else
			FxFiles.Stage(path, Encoding.UTF8.GetBytes(content));
		Files.Add(new(Display(path), write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	public static string Normalize(string text) => text.Replace("\r\n", "\n");

	/// <summary>UTF-8 without NUL characters: text.</summary>
	static bool IsText(byte[] content)
	{
		if (content.AsSpan().IndexOf((byte)0) >= 0)
			return false;
		try
		{
			_ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}
}
