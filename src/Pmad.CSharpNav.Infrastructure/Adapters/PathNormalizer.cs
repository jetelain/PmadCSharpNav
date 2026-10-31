namespace Pmad.CSharpNav.Infrastructure.Adapters;

public static class PathNormalizer
{
    public static string GetRelativePath(string solutionDir, string path)
    {
        if (string.IsNullOrWhiteSpace(solutionDir) || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(solutionDir, path);
        }
        catch
        {
            return path;
        }
    }
}

