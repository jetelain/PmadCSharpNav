namespace Pmad.CSharpNav.Infrastructure.Adapters;

public static class PathNormalizer
{
    public static string GetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            return normalized[..lastSlash];
        }
        return Path.GetDirectoryName(path) ?? "";
    }

    public static string GetRelativePath(string solutionDir, string path)
    {
        if (string.IsNullOrWhiteSpace(solutionDir) || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normDir = solutionDir.Replace('\\', '/').TrimEnd('/');
        var normPath = path.Replace('\\', '/');

        if (normPath.StartsWith(normDir + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = normPath[(normDir.Length + 1)..];
            return Path.DirectorySeparatorChar == '\\'
                ? relative.Replace('/', '\\')
                : relative;
        }

        try
        {
            var rel = Path.GetRelativePath(solutionDir, path);
            if (!Path.IsPathRooted(rel))
            {
                return rel;
            }
        }
        catch
        {
        }

        try
        {
            var rel = Path.GetRelativePath(normDir, normPath);
            return Path.DirectorySeparatorChar == '\\'
                ? rel.Replace('/', '\\')
                : rel;
        }
        catch
        {
            return path;
        }
    }
}

