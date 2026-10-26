namespace Pmad.CSharpNav.Cli.Formatting;

public static class ConsoleColorizer
{
    public static bool ShouldUseColor(bool noColorOption = false)
    {
        if (noColorOption)
        {
            return false;
        }

        if (Console.IsOutputRedirected)
        {
            return false;
        }

        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor))
        {
            return false;
        }

        return true;
    }
}
