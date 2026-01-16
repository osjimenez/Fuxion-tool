namespace NugetPackageManager;

using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Text;

internal static class Extensions
{
	extension<T>(IEnumerable<T> me)
	{
		internal void WriteAsTable()
		{
			// Create a table
			var table = new Table();

			var props = typeof(T).GetProperties();

			// Add some columns
			foreach (var prop in props)
			{
				table.AddColumn(prop.Name);
			}

			foreach (var version in me)
			{
				List<string> values = [];
				foreach (var prop in props)
				{
					var value = prop.GetValue(version)?.ToString() ?? "";
					values.Add(value);
				}
				// Add some rows
				table.AddRow(values.ToArray());
			}

			// Render the table to the console
			AnsiConsole.Write(table);
		}
	}
}
