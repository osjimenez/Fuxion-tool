using System;
using System.IO;
using System.Text;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Self;

// what fx self update left on Windows: the executable it replaced, which could not go while it ran
SelfUpdater.CleanUp(Environment.ProcessPath);
Console.OutputEncoding = Encoding.UTF8;
return FxApp.Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());
