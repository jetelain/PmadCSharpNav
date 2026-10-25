using System.CommandLine;
using System.Globalization;
using System.Text;

namespace Pmad.CSharpNav.Cli.Commands;

public static class SkillsCommand
{
    private const string ResourceName = "Pmad.CSharpNav.Cli.Skills.csharp-nav.SKILL.md";
    private static readonly Lazy<string> _skillMarkdownContent = new(LoadSkillMarkdown);

    public static string SkillMarkdownContent => _skillMarkdownContent.Value;

    private static string LoadSkillMarkdown()
    {
        var assembly = typeof(SkillsCommand).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Select(assembly.GetManifestResourceStream)
                .FirstOrDefault()
            ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Embedded resource for SKILL.md was not found in assembly '{0}'.", assembly.FullName));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static Command Create()
    {
        var skillsCmd = new Command("skills", "Manage AI coding agent skills for csnav");
        skillsCmd.Aliases.Add("skill");

        var installCmd = new Command("install", "Install the csharp-nav skill into agent configuration");
        var globalOption = new Option<bool>("--global") { Description = "Install globally into ~/.gemini/config/skills/ and ~/.agents/skills/" };
        globalOption.Aliases.Add("-g");

        var dirOption = new Option<string?>("--dir") { Description = "Custom destination directory for the skill" };
        dirOption.Aliases.Add("-d");

        installCmd.Add(globalOption);
        installCmd.Add(dirOption);

        installCmd.SetAction(async (parseResult, ct) =>
        {
            var isGlobal = parseResult.GetValue(globalOption);
            var customDir = parseResult.GetValue(dirOption);

            try
            {
                var targetDirs = new List<string>();

                if (!string.IsNullOrWhiteSpace(customDir))
                {
                    targetDirs.Add(Path.GetFullPath(customDir));
                }
                else if (isGlobal)
                {
                    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    targetDirs.Add(Path.Combine(userProfile, ".gemini", "config", "skills", "csharp-nav"));
                    targetDirs.Add(Path.Combine(userProfile, ".agents", "skills", "csharp-nav"));
                }
                else
                {
                    // Default: workspace level (.agents/skills/csharp-nav)
                    targetDirs.Add(Path.Combine(Directory.GetCurrentDirectory(), ".agents", "skills", "csharp-nav"));
                }

                foreach (var dir in targetDirs)
                {
                    Directory.CreateDirectory(dir);
                    var filePath = Path.Combine(dir, "SKILL.md");
                    await File.WriteAllTextAsync(filePath, SkillMarkdownContent, ct);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Successfully installed skill 'csharp-nav' at: {0}", filePath));
                }

                return 0;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(CultureInfo.InvariantCulture, "Error installing skill: {0}", ex.Message));
                return 1;
            }
        });

        var showCmd = new Command("show", "Display the csharp-nav SKILL.md definition to standard output");
        showCmd.Aliases.Add("cat");
        showCmd.Aliases.Add("view");

        showCmd.SetAction((parseResult) =>
        {
            Console.WriteLine(SkillMarkdownContent);
            return 0;
        });

        skillsCmd.Add(installCmd);
        skillsCmd.Add(showCmd);

        // Default behavior of running 'csnav skills' with no subcommands: run install
        skillsCmd.SetAction(async (parseResult, ct) =>
        {
            var targetDir = Path.Combine(Directory.GetCurrentDirectory(), ".agents", "skills", "csharp-nav");
            Directory.CreateDirectory(targetDir);
            var filePath = Path.Combine(targetDir, "SKILL.md");
            await File.WriteAllTextAsync(filePath, SkillMarkdownContent, ct);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Successfully installed skill 'csharp-nav' at: {0}", filePath));
            return 0;
        });

        return skillsCmd;
    }
}
