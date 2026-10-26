using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Cli.Formatting;

public static class PathHelper
{
    public static string? MakeRelative(string? path, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        baseDirectory ??= Directory.GetCurrentDirectory();

        try
        {
            var rel = Path.GetRelativePath(baseDirectory, path);

            // If it's a subpath of baseDirectory (doesn't start with ..)
            if (!rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
                rel != "..")
            {
                return rel;
            }

            // If relative path is shorter than the absolute path, use relative even if it starts with ..
            if (rel.Length < path.Length)
            {
                return rel;
            }

            return path;
        }
        catch
        {
            return path;
        }
    }

    public static FindReferencesResponse ToRelativePaths(this FindReferencesResponse response, string? baseDirectory = null)
    {
        baseDirectory ??= Directory.GetCurrentDirectory();
        var targetDef = MakeRelative(response.Target.DefinedIn, baseDirectory);
        var relTarget = response.Target with { DefinedIn = targetDef };
        var relRefs = response.References.Select(r =>
        {
            var relFile = MakeRelative(r.File, baseDirectory);
            return relFile != null && relFile != r.File ? r with { File = relFile } : r;
        }).ToList();

        return response with
        {
            Target = relTarget,
            References = relRefs
        };
    }

    public static FindDefinitionResponse ToRelativePaths(this FindDefinitionResponse response, string? baseDirectory = null)
    {
        baseDirectory ??= Directory.GetCurrentDirectory();
        var targetDef = MakeRelative(response.Target.DefinedIn, baseDirectory);
        var relTarget = response.Target with { DefinedIn = targetDef };
        var relDefs = response.Definitions.Select(d =>
        {
            var relFile = MakeRelative(d.File, baseDirectory);
            return relFile != null && relFile != d.File ? d with { File = relFile } : d;
        }).ToList();

        return response with
        {
            Target = relTarget,
            Definitions = relDefs
        };
    }

    public static FindImplementationsResponse ToRelativePaths(this FindImplementationsResponse response, string? baseDirectory = null)
    {
        baseDirectory ??= Directory.GetCurrentDirectory();
        var targetDef = MakeRelative(response.Target.DefinedIn, baseDirectory);
        var relTarget = response.Target with { DefinedIn = targetDef };
        var relImpls = response.Implementations.Select(i =>
        {
            var relFile = MakeRelative(i.File, baseDirectory);
            return relFile != null && relFile != i.File ? i with { File = relFile } : i;
        }).ToList();

        return response with
        {
            Target = relTarget,
            Implementations = relImpls
        };
    }
}
