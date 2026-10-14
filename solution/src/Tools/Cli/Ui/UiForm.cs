using System;
using System.Collections.Generic;
using System.Linq;
using XenoAtom.Terminal.UI;

namespace Fuxion.Tools.Cli.Ui;

/// <summary>A field of a form: its key, its label and its value, a text or a yes/no.</summary>
public sealed class UiField(string key, string label, string? text = null, bool? check = null)
{
	public string Key => key;

	public string Label => label;

	public State<string?> Text { get; } = new(text ?? "");

	/// <summary>The text, never null.</summary>
	public string Value => Text.Value ?? "";

	public State<bool> Check { get; } = new(check ?? false);

	public bool IsCheck => check is not null;

	/// <summary>The words of the text, split by commas and spaces (a list of tags, of repositories).</summary>
	public IReadOnlyList<string> Words => Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A form of <c>fx ui</c> (plan O, O4.3b): its fields and the edits they make, each with the command that does the
/// same (decision 10). The edits go to the pending changes; nothing is written until they are applied.
/// </summary>
public sealed class UiForm(string title, IReadOnlyList<UiField> fields, Func<UiForm, IReadOnlyList<UiEdit>> edits, string? file = null)
{
	public string Title => title;

	public IReadOnlyList<UiField> Fields => fields;

	/// <summary>The file it edits (full path): what its raw YAML shows (14.9).</summary>
	public string? File => file;

	public UiField this[string key] => fields.First(f => f.Key == key);

	/// <summary>What is wrong with the last try, if anything.</summary>
	public State<string> Error { get; } = new("");

	public IReadOnlyList<UiEdit> Edits() => edits(this);
}
