using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Dotnet;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>fx in Spanish (plan O, decision 7): the language is of the process, so this test goes alone.</summary>
[Collection(nameof(ProcessCollection))]
public sealed class DotnetSpanishTest
{
	[Fact(DisplayName = "fx in Spanish: the messages and the help are; codes, commands and JSON are not (plan O, decision 7)")]
	public void Cli_Spanish()
	{
		using var repo = new DotnetTest.Repo();
		try
		{
			var stdout = new StringWriter();
			Assert.Equal(1, InProcess.Run(["doctor", "dotnet", "--lang", "es"], stdout, new StringWriter(), repo.Git.Path));
			var doctor = stdout.ToString();
			Assert.Contains("[✗] dotnet     6 error(es)", doctor);
			Assert.Contains("Desfasado: fx sync lo cambiaría. (_fx/dotnet/dotnet.g.props · dotnet.outdated)", doctor);
			Assert.Contains("      → fx sync", doctor);
			Assert.Contains("El doctor ha encontrado problemas en 1 módulo(s).", doctor);

			stdout = new StringWriter();
			InProcess.Run(["doctor", "dotnet", "--lang", "es", "--output", "json"], stdout, new StringWriter(), repo.Git.Path);
			var outdated = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("diagnostics").EnumerateArray().First();
			Assert.Equal("Out of date: fx sync would change it.", outdated.GetProperty("message").GetString());

			stdout = new StringWriter();
			Assert.Equal(0, InProcess.Run(["sync", "--help", "--lang", "es"], stdout, new StringWriter(), repo.Git.Path));
			Assert.Contains("Genera y pone al día lo que es de fx aquí", stdout.ToString());
			Assert.Contains("--dry-run", stdout.ToString());

			// FX_LANG, and back to English
			stdout = new StringWriter();
			Environment.SetEnvironmentVariable(FxLanguage.Variable, "es");
			InProcess.Run(["doctor"], stdout, new StringWriter(), repo.Git.Path);
			Assert.Contains("Desfasado", stdout.ToString());
		}
		finally
		{
			Environment.SetEnvironmentVariable(FxLanguage.Variable, "en");
			FxLanguage.Apply("en");
		}
		var english = new StringWriter();
		InProcess.Run(["doctor"], english, new StringWriter(), repo.Git.Path);
		Assert.Contains("Out of date: fx sync would change it.", english.ToString());
	}
}
