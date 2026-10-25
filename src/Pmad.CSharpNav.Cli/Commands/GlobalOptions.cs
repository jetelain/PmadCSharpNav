using System.CommandLine;

namespace Pmad.CSharpNav.Cli.Commands;

public sealed class GlobalOptions
{
    public Option<string?> SolutionOption { get; } = new("--solution")
    {
        Description = "Path to .slnx, .sln, .csproj or directory (auto-detected if omitted)",
        Recursive = true
    };

    public Option<string?> FrameworkOption { get; } = new("--framework")
    {
        Description = "Target framework moniker filter (e.g. net10.0)",
        Recursive = true
    };

    public Option<string> FormatOption { get; } = new("--format")
    {
        Description = "Output format: json (default), text, or markdown",
        DefaultValueFactory = _ => "json",
        Recursive = true
    };

    public GlobalOptions()
    {
        SolutionOption.Aliases.Add("-s");
        FrameworkOption.Aliases.Add("-f");
    }

    public void AddTo(RootCommand root)
    {
        root.Add(SolutionOption);
        root.Add(FrameworkOption);
        root.Add(FormatOption);
    }
}
