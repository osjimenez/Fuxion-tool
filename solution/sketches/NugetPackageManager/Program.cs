using System.Xml.Linq;
using Fuxion;
using NugetPackageManager;
using Spectre.Console;

Console.WriteLine("Starting ...");

List<Package> packages =
[
	new("Fuxion", ListMatch, DeprecationMatch),
	new("Fuxion.Application", ListMatch, DeprecationMatch),
	new("Fuxion.Analyzers", ListMatch, DeprecationMatch),
	new("Fuxion.Analyzers.Abstractions", ListMatch, DeprecationMatch),
	new("Fuxion.AspNet", ListMatch, DeprecationMatch),
	new("Fuxion.AspNetCore", ListMatch, DeprecationMatch),
	new("Fuxion.AutoMapper", ListMatch, DeprecationMatch),
	new("Fuxion.Domain", ListMatch, DeprecationMatch),
	new("Fuxion.Drawing", ListMatch, DeprecationMatch),
	new("Fuxion.EntityFramework", ListMatch, DeprecationMatch),
	new("Fuxion.EntityFrameworkCore", ListMatch, DeprecationMatch),
	new("Fuxion.EntityFrameworkCore.InMemory", ListMatch, DeprecationMatch),
	new("Fuxion.EntityFrameworkCore.SqlServer", ListMatch, DeprecationMatch),
	new("Fuxion.EventStore", ListMatch, DeprecationMatch),
	new("Fuxion.Identity", ListMatch, DeprecationMatch),
	new("Fuxion.Licensing", ListMatch, DeprecationMatch),
	new("Fuxion.Log4net", ListMatch, DeprecationMatch),
	new("Fuxion.MassTransit", ListMatch, DeprecationMatch),
	new("Fuxion.MongoDB", ListMatch, DeprecationMatch),
	new("Fuxion.Orleans", ListMatch, DeprecationMatch),
	new("Fuxion.Pods", ListMatch, DeprecationMatch),
	new("Fuxion.RabbitMQ", ListMatch, DeprecationMatch),
	new("Fuxion.Shell", ListMatch, DeprecationMatch),
	new("Fuxion.Windows", ListMatch, DeprecationMatch),
	new("Fuxion.Xunit", ListMatch, DeprecationMatch),
];

var client = new NugetClient();

await client.FillDataAsync(packages);

var toList = packages
	.SelectMany(p => p.Versions)
	.Where(v => v.Action.HasFlag(NugetAction.List))
	.ToList();

if (toList.Any())
{
	toList.WriteAsTable();
	// Ask the user to confirm
	var listConfirmation = AnsiConsole.Prompt(
		new TextPrompt<bool>("Do you want to list these packages?")
			.AddChoice(true)
			.AddChoice(false)
			.DefaultValue(false)
			.WithConverter(choice => choice ? "y" : "n"));
	if (listConfirmation)
	{
		foreach (var version in toList)
		{
			var res = await client.List(version);
			if (res is not Error error)
				Console.WriteLine($"Listed {version.Package.Id} {version.Version}");
			else
			{
				Console.WriteLine($"Error listing {version.Package.Id} {version.Version}: {error.Message}");
				if (error.Message.IsNeitherNullNorWhiteSpace())
				{
					var panel = new Panel(XElement.Load(error.Message).ToString());
					AnsiConsole.Write(panel);
				}
			}
		}
	}
}

var toUnlist = packages
	.SelectMany(p => p.Versions)
	.Where(v => v.Action.HasFlag(NugetAction.Unlist))
	.ToList();

if (toUnlist.Any())
{
	toUnlist.WriteAsTable();
	// Ask the user to confirm
	var listConfirmation = AnsiConsole.Prompt(
		new TextPrompt<bool>("Do you want to unlist these packages?")
			.AddChoice(true)
			.AddChoice(false)
			.DefaultValue(false)
			.WithConverter(choice => choice ? "y" : "n"));
	if (listConfirmation)
	{
		foreach (var version in toUnlist)
		{
			var res = await client.Unlist(version);
			if (res is not Error error)
				Console.WriteLine($"Unlisted {version.Package.Id} {version.Version}");
			else
			{
				Console.WriteLine($"Error unlisting {version.Package.Id} {version.Version}: {error.Message}");
				if (error.Message.IsNeitherNullNorWhiteSpace())
				{
					// { "statusCode": 403, "message": "Out of call volume quota. Quota will be replenished in 00:33:45." }
					var panel = new Panel(XElement.Load(error.Message).ToString());
					AnsiConsole.Write(panel);
				}
			}
		}
	}
}

packages
	.SelectMany(p => p.Versions)
	.Where(v => v.Action.HasFlag(NugetAction.Deprecate) || v.Action.HasFlag(NugetAction.Undeprecate))
	.WriteAsTable();

Console.WriteLine("Finished");

return;

// To list versions less than 8.2.14 except 0.2.0 and 0.2.1
bool ListMatch(SemanticVersion version)
{
	return version != "10.0.1" && version >= "9.3.34" || version == "8.2.14" || version == "0.2.0" || version == "0.2.1";
}

// To deprecate versions less than 9.0.0
bool DeprecationMatch(SemanticVersion version)
{
	return version < "10.0.2";
}