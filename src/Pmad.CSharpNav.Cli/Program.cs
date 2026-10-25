using System.CommandLine;
using Pmad.CSharpNav.Cli.Client;
using Pmad.CSharpNav.Cli.Commands;

var root = new RootCommand("csnav: High-performance C# navigation engine for AI agents and developers");

var globals = new GlobalOptions();
globals.AddTo(root);

using var client = new NavDaemonClient();

root.Add(RefsCommand.Create(globals, client));
root.Add(ApiCommand.Create(globals, client));
root.Add(ExploreCommand.Create(globals, client));
root.Add(DecompileCommand.Create(globals, client));
root.Add(ServerCommand.Create(globals, client));
root.Add(SkillsCommand.Create());

return await root.Parse(args).InvokeAsync();
