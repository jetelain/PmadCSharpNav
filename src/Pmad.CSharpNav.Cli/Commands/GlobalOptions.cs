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
        Description = "Output format: markdown (default), json, or text (can also be configured via CSNAV_FORMAT or CSNAV_DEFAULT_FORMAT)",
        DefaultValueFactory = _ => GetDefaultFormat(),
        Recursive = true
    };

    public Option<bool> NoColorOption { get; } = new("--no-color")
    {
        Description = "Disable ANSI color formatting in output",
        DefaultValueFactory = _ => false,
        Recursive = true
    };

    public GlobalOptions()
    {
        SolutionOption.Aliases.Add("-s");
        FrameworkOption.Aliases.Add("-f");
    }

    public static string GetDefaultFormat()
    {
        var env = Environment.GetEnvironmentVariable("CSNAV_FORMAT");
        if (string.IsNullOrWhiteSpace(env))
        {
            env = Environment.GetEnvironmentVariable("CSNAV_DEFAULT_FORMAT");
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            var trimmed = env.Trim().ToLowerInvariant();
            if (trimmed is "markdown" or "json" or "text")
            {
                return trimmed;
            }
        }

        return "markdown";
    }

    public void AddTo(RootCommand root)
    {
        root.Add(SolutionOption);
        root.Add(FrameworkOption);
        root.Add(FormatOption);
        root.Add(NoColorOption);
    }
}
