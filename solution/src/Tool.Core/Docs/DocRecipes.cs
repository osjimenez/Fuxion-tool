using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuxion.Tool.Core.Docs;

/// <summary>The recipes of the views of fx (<see cref="DocViews"/>), the <c>recipe</c> of their <c>generation</c>.</summary>
public static class DocRecipes
{
	/// <summary>The dashboard of a folder: what asks for attention, what is open and what is in force, by type.</summary>
	public const string Status = "docs.status";

	/// <summary>The documents of a folder; <c>docs.catalog/&lt;tag&gt;</c>, only those with the tag.</summary>
	public const string Catalog = "docs.catalog";

	/// <summary>The board of a register: its open entries by status, and those closed.</summary>
	public const string Backlog = "docs.backlog";

	public static readonly IReadOnlyList<string> All = [Status, Catalog, Backlog];

	/// <summary>Whether <paramref name="recipe"/> is one of fx: one of <see cref="All"/>, or <c>docs.catalog/&lt;tag&gt;</c>.</summary>
	public static bool Known(string recipe) => All.Contains(recipe) || recipe.StartsWith($"{Catalog}/", StringComparison.Ordinal) && recipe.Length > Catalog.Length + 1;
}
